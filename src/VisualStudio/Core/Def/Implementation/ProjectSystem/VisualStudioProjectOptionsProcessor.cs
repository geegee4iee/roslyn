﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host;
using Roslyn.Utilities;

namespace Microsoft.VisualStudio.LanguageServices.Implementation.ProjectSystem
{
    internal class VisualStudioProjectOptionsProcessor : IDisposable
    {
        private readonly VisualStudioProject _project;
        private readonly HostWorkspaceServices _workspaceServices;
        private readonly ICommandLineParserService _commandLineParserService;
        private readonly ITemporaryStreamStorage _commandLineStorage;

        /// <summary>
        /// Gate to guard all mutable fields in this class.
        /// The lock hierarchy means you are allowed to call out of this class and into <see cref="_project"/> while holding the lock.
        /// </summary>
        private readonly object _gate = new object();

        /// <summary>
        /// A hashed checksum of the last command line we were set to.  We use this
        /// as a low cost (in terms of memory) way to determine if the command line
        /// actually changes and we need to make any downstream updates.
        /// </summary>
        private Checksum _commandLineChecksum;

        private CommandLineArguments _commandLineArgumentsForCommandLine;
        private string _explicitRuleSetFilePath;
        private IReferenceCountedDisposable<ICacheEntry<string, IRuleSetFile>> _ruleSetFile = null;

        public VisualStudioProjectOptionsProcessor(
            VisualStudioProject project,
            HostWorkspaceServices workspaceServices)
        {
            _project = project ?? throw new ArgumentNullException(nameof(project));
            _workspaceServices = workspaceServices;
            _commandLineParserService = workspaceServices.GetLanguageServices(project.Language).GetRequiredService<ICommandLineParserService>();
            var temporaryStorageService = workspaceServices.GetService<ITemporaryStorageService>();
            _commandLineStorage = temporaryStorageService.CreateTemporaryStreamStorage();

            // Set up _commandLineArgumentsForCommandLine to a default. No lock taken since we're in
            // the constructor so nothing can race.
            ReparseCommandLineIfChanged_NoLock(commandLine: "");
        }

        /// <returns><see langword="true"/> if the command line was updated.</returns>
        private bool ReparseCommandLineIfChanged_NoLock(string commandLine)
        {
            var checksum = Checksum.Create(commandLine);
            if (_commandLineChecksum == checksum)
                return false;

            // Persist the command line to our storage service so we can recover it later while not
            // having to keep it all in memory.  This is valuable in some scenarios where the
            // command line can actually end up being enormous (i.e. because of tons of dlls that
            // are referenced).
            using (var memoryStream = new MemoryStream())
            using (var streamWriter = new StreamWriter(memoryStream))
            {
                streamWriter.Write(commandLine);
                streamWriter.Flush();
                memoryStream.Position = 0;
                _commandLineStorage.WriteStream(memoryStream);
            }

            _commandLineChecksum = checksum;
            ReparseCommandLine_NoLock(commandLine);
            return true;
        }

        public void SetCommandLine(string commandLine)
        {
            if (commandLine == null)
                throw new ArgumentNullException(nameof(commandLine));

            lock (_gate)
            {
                // If we actually got a new command line, then update the project options, otherwise
                // we don't need to do anything.
                if (ReparseCommandLineIfChanged_NoLock(commandLine))
                {
                    UpdateProjectOptions_NoLock();
                }
            }
        }

        public string ExplicitRuleSetFilePath
        {
            get => _explicitRuleSetFilePath;

            set
            {
                lock (_gate)
                {
                    if (_explicitRuleSetFilePath == value)
                    {
                        return;
                    }

                    _explicitRuleSetFilePath = value;

                    UpdateProjectOptions_NoLock();
                }
            }
        }

        /// <summary>
        /// Returns the active path to the rule set file that is being used by this project, or null if there isn't a rule set file.
        /// </summary>
        public string EffectiveRuleSetFilePath
        {
            get
            {
                // We take a lock when reading this because we might be in the middle of processing a file update on another
                // thread.
                lock (_gate)
                {
                    return _ruleSetFile?.Target.Value.FilePath;
                }
            }
        }

        private void DisposeOfRuleSetFile_NoLock()
        {
            if (_ruleSetFile != null)
            {
                _ruleSetFile.Target.Value.UpdatedOnDisk -= RuleSetFile_UpdatedOnDisk;
                _ruleSetFile.Dispose();
                _ruleSetFile = null;
            }
        }

        private void ReparseCommandLine_NoLock(string commandLine)
        {
            var arguments = CommandLineParser.SplitCommandLineIntoArguments(commandLine, removeHashComments: false);
            _commandLineArgumentsForCommandLine = _commandLineParserService.Parse(arguments, Path.GetDirectoryName(_project.FilePath), isInteractive: false, sdkDirectory: null);
        }

        private void UpdateProjectOptions_NoLock()
        {
            var effectiveRuleSetPath = ExplicitRuleSetFilePath ?? _commandLineArgumentsForCommandLine.RuleSetPath;

            if (_ruleSetFile?.Target.Value.FilePath != effectiveRuleSetPath)
            {
                // We're changing in some way. Be careful: this might mean the path is switching to or from null, so either side so far
                // could be changed.
                DisposeOfRuleSetFile_NoLock();

                if (effectiveRuleSetPath != null)
                {
                    _ruleSetFile = _workspaceServices.GetRequiredService<VisualStudioRuleSetManager>().GetOrCreateRuleSet(effectiveRuleSetPath);
                    _ruleSetFile.Target.Value.UpdatedOnDisk += RuleSetFile_UpdatedOnDisk;
                }
            }

            // TODO: #r support, should it include bin path?
            var referenceSearchPaths = ImmutableArray<string>.Empty;

            // TODO: #load support
            var sourceSearchPaths = ImmutableArray<string>.Empty;

            var referenceResolver = new WorkspaceMetadataFileReferenceResolver(
                    _workspaceServices.GetRequiredService<IMetadataService>(),
                    new RelativePathResolver(referenceSearchPaths, _commandLineArgumentsForCommandLine.BaseDirectory));

            var compilationOptions = _commandLineArgumentsForCommandLine.CompilationOptions
                .WithConcurrentBuild(concurrent: false)
                .WithMetadataReferenceResolver(referenceResolver)
                .WithXmlReferenceResolver(new XmlFileResolver(_commandLineArgumentsForCommandLine.BaseDirectory))
                .WithSourceReferenceResolver(new SourceFileResolver(sourceSearchPaths, _commandLineArgumentsForCommandLine.BaseDirectory))
                .WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default)
                .WithStrongNameProvider(new DesktopStrongNameProvider(_commandLineArgumentsForCommandLine.KeyFileSearchPaths.WhereNotNull().ToImmutableArray()));

            // Override the default documentation mode.
            var documentationMode = _commandLineArgumentsForCommandLine.DocumentationPath != null ? DocumentationMode.Diagnose : DocumentationMode.Parse;
            var parseOptions = _commandLineArgumentsForCommandLine.ParseOptions
                .WithDocumentationMode(documentationMode);

            // We've computed what the base values should be; we now give an opportunity for any host-specific settings to be computed
            // before we apply them
            compilationOptions = ComputeCompilationOptionsWithHostValues(compilationOptions, this._ruleSetFile?.Target.Value);
            parseOptions = ComputeParseOptionsWithHostValues(parseOptions);

            // For managed projects, AssemblyName has to be non-null, but the command line we get might be a partial command line
            // and not contain the existing value. Only update if we have one.
            _project.AssemblyName = _commandLineArgumentsForCommandLine.CompilationName ?? _project.AssemblyName;
            _project.CompilationOptions = compilationOptions;

            var fullIntermediateOutputPath = _commandLineArgumentsForCommandLine.OutputDirectory != null && _commandLineArgumentsForCommandLine.OutputFileName != null
                                                    ? Path.Combine(_commandLineArgumentsForCommandLine.OutputDirectory, _commandLineArgumentsForCommandLine.OutputFileName)
                                                    : _commandLineArgumentsForCommandLine.OutputFileName;

            _project.IntermediateOutputFilePath = fullIntermediateOutputPath ?? _project.IntermediateOutputFilePath;
            _project.ParseOptions = parseOptions;
        }

        private void RuleSetFile_UpdatedOnDisk(object sender, EventArgs e)
        {
            lock (_gate)
            {
                // This event might have gotten fired "late" if the file change was already in flight. We can see if this is still our current file;
                // it won't be if this is disposed or was already changed to a different file. We hard-cast sender to an IRuleSetFile because if it's
                // something else that means our comparison below is definitely broken.
                if (_ruleSetFile?.Target.Value != (IRuleSetFile)sender)
                {
                    return;
                }

                // The IRuleSetFile held by _ruleSetFile is now out of date. We'll dispose our old one first so as to let go of any old cached values.
                // Then, we must reparse: in the case where the command line we have from the project system includes a /ruleset, the computation of the
                // effective values was potentially done by the act of parsing the command line. Even though the command line didn't change textually,
                // the effective result did. Then we call UpdateProjectOptions_NoLock to reapply any values; that will also re-acquire the new ruleset
                // includes in the IDE so we can be watching for changes again.
                using var commandLineStream = _commandLineStorage.ReadStream();
                using var stringStream = new StreamReader(commandLineStream);
                var commandLine = stringStream.ReadToEnd();

                DisposeOfRuleSetFile_NoLock();
                ReparseCommandLine_NoLock(commandLine);
                UpdateProjectOptions_NoLock();
            }
        }

        /// <summary>
        /// Overridden by derived classes to provide a hook to modify a <see cref="CompilationOptions"/> with any host-provided values that didn't come from
        /// the command line string.
        /// </summary>
        protected virtual CompilationOptions ComputeCompilationOptionsWithHostValues(CompilationOptions compilationOptions, IRuleSetFile ruleSetFileOpt)
        {
            return compilationOptions;
        }

        /// <summary>
        /// Override by derived classes to provide a hook to modify a <see cref="ParseOptions"/> with any host-provided values that didn't come from 
        /// the command line string.
        /// </summary>
        protected virtual ParseOptions ComputeParseOptionsWithHostValues(ParseOptions parseOptions)
        {
            return parseOptions;
        }

        /// <summary>
        /// Called by a derived class to notify that we need to update the settings in the project system for something that will be provided
        /// by either <see cref="ComputeCompilationOptionsWithHostValues(CompilationOptions, IRuleSetFile)"/> or <see cref="ComputeParseOptionsWithHostValues(ParseOptions)"/>.
        /// </summary>
        protected void UpdateProjectForNewHostValues()
        {
            lock (_gate)
            {
                UpdateProjectOptions_NoLock();
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                DisposeOfRuleSetFile_NoLock();
            }
        }
    }
}
