using System;
using System.ComponentModel.Design;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Conan.VisualStudio.Menu;
using Conan.VisualStudio.Services;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.VCProjectEngine;
using Conan.VisualStudio.Core;

namespace Conan.VisualStudio
{
    /// <summary>This is the class that implements the package exposed by this assembly.</summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("#110", "#112", "1.0", IconResourceID = 400)] // Info on this package for Help/About
    [Guid(PackageGuidString)]
    [SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1650:ElementDocumentationMustBeSpelledCorrectly", Justification = "pkgdef, VS and vsixmanifest are valid VS terms")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    // Indicate we want to load whenever VS opens, so that we can hopefully catch the Solution_Opened event
    [ProvideAutoLoad(UIContextGuids80.NoSolution, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(UIContextGuids80.SolutionHasMultipleProjects, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(UIContextGuids80.SolutionHasSingleProject, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(UIContextGuids80.EmptySolution, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideOptionPage(typeof(ConanOptionsPage), "Conan", "Main", 0, 0, true)]
    [ProvideToolWindow(typeof(PackageListToolWindow))]
    public sealed class VSConanPackage : AsyncPackage, IVsUpdateSolutionEvents3
    {
        /// <summary>
        /// VSConanPackage GUID string.
        /// </summary>
        public const string PackageGuidString = "33315c89-72dd-43bb-863c-561c1aa5ed54";

        private AddConanDepends _addConanDepends;
        private ShowPackageListCommand _showPackageListCommand;
        private IntegrateIntoProjectCommand _integrateIntoProjectCommand;
        private DTE _dte;
        private SolutionEvents _solutionEvents;
        private IVsSolution _solution;
        private SolutionEventsHandler _solutionEventsHandler;
        private ISettingsService _settingsService;
        private IVcProjectService _vcProjectService;
        private IConanService _conanService;
        private IVsSolutionBuildManager3 _solutionBuildManager;

        /// <summary>
        /// Initialization of the package; this method is called right after the package is sited, so this is the place
        /// where you can put all the initialization code that rely on services provided by VisualStudio.
        /// </summary>
        protected override async System.Threading.Tasks.Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress);

            _dte = await GetServiceAsync<DTE>();

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            _solution = await GetServiceAsync<SVsSolution>() as IVsSolution;
            _solutionBuildManager = await GetServiceAsync<IVsSolutionBuildManager>() as IVsSolutionBuildManager3;

            var serviceProvider = new ServiceProvider((Microsoft.VisualStudio.OLE.Interop.IServiceProvider)_dte);

            await TaskScheduler.Default;

            var dialogService = new VisualStudioDialogService(serviceProvider);
            var commandService = await GetServiceAsync<IMenuCommandService>();
            _vcProjectService = new VcProjectService();
            _settingsService = new VisualStudioSettingsService(this);
            _conanService = new ConanService(_settingsService, dialogService, _vcProjectService);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            _solutionEventsHandler = new SolutionEventsHandler(this);
            _solution.AdviseSolutionEvents(_solutionEventsHandler, out var _solutionEventsCookie);

            _addConanDepends = new AddConanDepends(commandService, dialogService, _vcProjectService, _settingsService, serviceProvider, _conanService);

            await TaskScheduler.Default;

            _showPackageListCommand = new ShowPackageListCommand(this, commandService, dialogService);
            _integrateIntoProjectCommand = new IntegrateIntoProjectCommand(commandService, dialogService, _vcProjectService, _settingsService, _conanService);

            Logger.Initialize(serviceProvider, "Conan");

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            SubscribeToEvents();

            EnableMenus(_dte.Solution != null && _dte.Solution.IsOpen);

            await TaskScheduler.Default;
        }

        private void EnableMenus(bool enable)
        {
            _showPackageListCommand.EnableMenu(enable);
            _integrateIntoProjectCommand.EnableMenu(enable);
            _addConanDepends.EnableMenu(enable);
        }

        private async Task<T> GetServiceAsync<T>() where T : class =>
            await GetServiceAsync(typeof(T)) as T ?? throw new Exception($"Cannot initialize service {typeof(T).FullName}");

        /// <summary>
        /// Use the DTE object to gain access to Solution events
        /// </summary>
        private void SubscribeToEvents()
        {
            /**
             * Note that _solutionEvents is not a local variable but a class variable
             * to prevent from Visual Studio garbage collecting our variable which would
             * mean missed events.
            **/

            ThreadHelper.ThrowIfNotOnUIThread();

            _solutionEvents = _dte.Events.SolutionEvents;

            /**
             * SolutionEvents_Opened should give us an event for opened solutions and projects
             * according to https://docs.microsoft.com/en-us/dotnet/api/envdte.solutioneventsclass.opened?view=visualstudiosdk-2017
             */
            _solutionEvents.Opened += SolutionEvents_Opened;
            _solutionEvents.AfterClosing += SolutionEvents_AfterClosing;
            _solutionEvents.ProjectAdded += SolutionEvents_ProjectAdded;

            if (_solutionBuildManager != null)
            {
                uint pdwcookie = 0;
                _solutionBuildManager.AdviseUpdateSolutionEvents3(this, out pdwcookie);
            }
        }

        public int OnBeforeActiveSolutionCfgChange(IVsCfg pOldActiveSlnCfg, IVsCfg pNewActiveSlnCfg)
        {
            return VSConstants.S_OK;
        }

        public int OnAfterActiveSolutionCfgChange(IVsCfg pOldActiveSlnCfg, IVsCfg pNewActiveSlnCfg)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_settingsService.GetConanInstallOnlyActiveConfiguration())
                InstallConanDepsIfRequired();
            return VSConstants.S_OK;
        }

        private void InstallConanDeps(VCProject vcProject)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(
                async delegate
                {
                    await _conanService.InstallAsync(vcProject);
                    await _conanService.IntegrateAsync(vcProject);
                }
            );
        }

        private void SolutionEvents_AfterClosing()
        {
            EnableMenus(false);
        }

        private void SolutionEvents_ProjectAdded(Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_settingsService.GetConanInstallAutomatically())
            {
                if (_vcProjectService.IsConanProject(project))
                    InstallConanDeps(_vcProjectService.AsVCProject(project));
            }
        }

        private void InstallConanDepsIfRequired()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_settingsService.GetConanInstallAutomatically())
            {
                foreach (Project project in _dte.Solution.Projects)
                {
                    if (_vcProjectService.IsConanProject(project))
                        InstallConanDeps(_vcProjectService.AsVCProject(project));
                }
            }
        }

        /// <summary>
        /// Handler to react on a solution opened event
        /// </summary>
        private void SolutionEvents_Opened()
        {
            /**
             * Get all projects within the solution
             */
            ThreadHelper.ThrowIfNotOnUIThread();

            EnableMenus(true);

            var projects = _dte.Solution.Projects;

            /**
             * For each project call Conan
             */
            foreach (Project project in projects)
            {
                /*
                 * This would be the place to start reading the project-specific JSON file
                 * to determine what command to run. At this stage we have a <see cref="Project"/> object
                 * of which we can use the FileName property to use in the command.
                 */
                var fileName = project.FileName;
            }
            InstallConanDepsIfRequired();
        }
    }
}
