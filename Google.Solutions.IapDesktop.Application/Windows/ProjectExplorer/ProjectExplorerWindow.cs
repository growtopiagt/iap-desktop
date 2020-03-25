﻿using Google.Apis.Compute.v1.Data;
using Google.Solutions.Compute.Iap;
using Google.Solutions.Compute.Net;
using Google.Solutions.IapDesktop.Application.Adapters;
using Google.Solutions.IapDesktop.Application.ObjectModel;
using Google.Solutions.IapDesktop.Application.Services;
using Google.Solutions.IapDesktop.Application.Settings;
using Google.Solutions.IapDesktop.Application.SettingsEditor;
using Google.Solutions.IapDesktop.Application.Windows;
using Google.Solutions.IapDesktop.Application.Windows.RemoteDesktop;
using Google.Solutions.IapDesktop.Windows;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using WeifenLuo.WinFormsUI.Docking;

namespace Google.Solutions.IapDesktop.Application.ProjectExplorer
{
    public partial class ProjectExplorerWindow : ToolWindow, IProjectExplorer
    {
        private const int RemoteDesktopPort = 3389;

        private readonly DockPanel dockPanel;
        private readonly IMainForm mainForm;
        private readonly IEventService eventService;
        private readonly IJobService jobService;
        private readonly ProjectInventoryService projectInventoryService;
        private readonly InventorySettingsRepository settingsRepository;
        private readonly IAuthorizationService authService;
        private readonly IServiceProvider serviceProvider;
        private readonly TunnelBrokerService tunnelBrokerService;
        private readonly RemoteDesktopService remoteDesktopService;

        private readonly CloudNode rootNode = new CloudNode();


        public ProjectExplorerWindow(IServiceProvider serviceProvider)
        {
            InitializeComponent();

            this.dockPanel = serviceProvider.GetService<IMainForm>().MainPanel;
            this.serviceProvider = serviceProvider;

            this.TabText = this.Text;

            //
            // This window is a singleton, so we never want it to be closed,
            // just hidden.
            //
            this.HideOnClose = true;

            this.vsToolStripExtender.SetStyle(
                this.toolStrip,
                VisualStudioToolStripExtender.VsVersion.Vs2015,
                this.vs2015LightTheme);

            this.treeView.Nodes.Add(this.rootNode);

            this.mainForm = serviceProvider.GetService<IMainForm>();
            this.eventService = serviceProvider.GetService<IEventService>();
            this.jobService = serviceProvider.GetService<IJobService>();
            this.projectInventoryService = serviceProvider.GetService<ProjectInventoryService>();
            this.settingsRepository = serviceProvider.GetService<InventorySettingsRepository>();
            this.authService = serviceProvider.GetService<IAuthorizationService>();
            this.tunnelBrokerService = serviceProvider.GetService<TunnelBrokerService>();
            this.remoteDesktopService = serviceProvider.GetService<RemoteDesktopService>();

            this.eventService.BindAsyncHandler<ProjectInventoryService.ProjectAddedEvent>(OnProjectAdded);
            this.eventService.BindHandler<ProjectInventoryService.ProjectDeletedEvent>(OnProjectDeleted);
        }

        private void PopulateProjectNode(string projectId, IEnumerable<Instance> instances)
        {
            Debug.Assert(!this.InvokeRequired);

            var projectNode = this.rootNode.Nodes
                .Cast<ProjectNode>()
                .FirstOrDefault(n => n.ProjectId == projectId);
            if (projectNode != null)
            {
                projectNode.Populate(instances);
            }
            else
            {
                projectNode = new ProjectNode(this.settingsRepository, projectId);
                projectNode.Populate(instances);
                this.rootNode.Nodes.Add(projectNode);
            }

            this.rootNode.Expand();
        }

        private async Task AddProjectAsync()
        {
            await this.jobService.RunInBackground(
                new JobDescription("Loading projects..."),
                _ => this.authService.Authorization.Credential.GetAccessTokenForRequestAsync());

            // Show project picker
            var dialog = this.serviceProvider.GetService<ProjectPickerDialog>();
            string projectId = projectId = dialog.SelectProjectId(this);

            if (projectId == null)
            {
                // Cancelled.
                return;
            }

            await this.projectInventoryService.AddProjectAsync(projectId);

        }

        private async Task GenerateCredentials(VmInstanceNode vmNode)
        {
            // Derive a suggested username from the Windows login name.
            var suggestedUsername = Environment.UserName;

            // Prompt for username to use.
            var username = new GenerateCredentialsDialog().PromptForUsername(this, suggestedUsername);
            if (username == null)
            {
                return;
            }

            var credentials = await this.jobService.RunInBackground(
                new JobDescription("Generating Windows logon credentials..."),
                token =>
                {
                    return this.serviceProvider.GetService<IComputeEngineAdapter>()
                        .ResetWindowsUserAsync(vmNode.Reference, username, token);
                });

            new ShowCredentialsDialog().ShowDialog(
                this,
                credentials.UserName,
                credentials.Password);

            // Update node to persist settings.
            vmNode.Username = credentials.UserName;
            vmNode.Password = credentials.Password;
            vmNode.Domain = null;
            vmNode.SaveChanges();

            // Fire an event to update anybody using the node.
            await this.eventService.FireAsync(new ProjectExplorerNodeSelectedEvent(vmNode));
        }
        private async Task ConnectInstance(VmInstanceNode vmNode)
        {
            var destination = new TunnelDestination(vmNode.Reference, RemoteDesktopPort);

            // TODO: make configurable
            var timeout = TimeSpan.FromSeconds(30);

            var tunnel = await this.jobService.RunInBackground(
                new JobDescription("Opening Cloud IAP tunnel..."),
                async token =>
                {
                    try
                    {
                        return await this.tunnelBrokerService.ConnectAsync(destination, timeout);
                    }
                    catch (NetworkStreamClosedException e)
                    {
                        throw new ApplicationException(
                            "Connecting to the instance failed. Make sure that you have " +
                            "configured your firewall rules to permit Cloud IAP access " +
                            $"to {vmNode.InstanceName}",
                            e);
                    }
                    catch (UnauthorizedException)
                    {
                        throw new ApplicationException("You are not authorized to connect to this VM instance");
                    }
                });

            this.remoteDesktopService.Connect(
                vmNode.Reference,
                "localhost",
                (ushort)tunnel.LocalPort,
                vmNode.Settings);
        }

        //---------------------------------------------------------------------
        // Context menu event handlers.
        //---------------------------------------------------------------------

        private void treeView_NodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                this.treeView.SelectedNode = e.Node;
            }
        }

        private async void refreshAllProjectsToolStripMenuItem_Click(object sender, EventArgs _)
        {
            try
            {
                await RefreshAllProjects();
            }
            catch (TaskCanceledException)
            {
                // Ignore.
            }
            catch (Exception e)
            {
                this.serviceProvider
                    .GetService<IExceptionDialog>()
                    .Show(this, "Refreshing project failed", e);
            }
        }

        private async void refreshToolStripMenuItem_Click(object sender, EventArgs _)
        {
            try
            {
                if (this.treeView.SelectedNode is ProjectNode projectNode)
                {
                    await RefreshProject(projectNode.ProjectId);
                }
            }
            catch (TaskCanceledException)
            {
                // Ignore.
            }
            catch (Exception e)
            {
                this.serviceProvider
                    .GetService<IExceptionDialog>()
                    .Show(this, "Refreshing project failed", e);
            }
        }

        private async void unloadProjectToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (this.treeView.SelectedNode is ProjectNode projectNode)
            {
                await this.projectInventoryService.DeleteProjectAsync(projectNode.ProjectId);
            }
        }

        private void propertiesToolStripMenuItem_Click(object sender, EventArgs e)
            => openSettingsButton_Click(sender, e);

        private void openInCloudConsoleToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (this.treeView.SelectedNode is VmInstanceNode vmInstanceNode)
            {
                this.serviceProvider
                    .GetService<CloudConsoleService>()
                    .OpenVmInstance(vmInstanceNode.Reference);
            }
        }

        private void openlogsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (this.treeView.SelectedNode is VmInstanceNode vmInstanceNode)
            {
                this.serviceProvider
                    .GetService<CloudConsoleService>()
                    .OpenVmInstanceLogs(vmInstanceNode.Reference, vmInstanceNode.InstanceId);
            }
        }

        private void configureIapAccessToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (this.treeView.SelectedNode is ProjectNode projectNode)
            {
                this.serviceProvider
                    .GetService<CloudConsoleService>()
                    .ConfigureIapAccess(projectNode.ProjectId);
            }
            else if (this.treeView.SelectedNode is VmInstanceNode vmInstanceNode)
            {
                this.serviceProvider
                    .GetService<CloudConsoleService>()
                    .ConfigureIapAccess(vmInstanceNode.ProjectId);
            }
        }

        private void generateCredentialsToolStripMenuItem_Click(object sender, EventArgs e)
            => generateCredentialsToolStripButton_Click(sender, e);

        private void connectToolStripMenuItem_Click(object sender, EventArgs e)
            => connectToolStripButton_Click(sender, e);

        private void treeView_NodeMouseDoubleClick(object sender, TreeNodeMouseClickEventArgs e)
            => connectToolStripButton_Click(sender, e);

        //---------------------------------------------------------------------
        // Tool bar event handlers.
        //---------------------------------------------------------------------

        private async void refreshButton_Click(object sender, EventArgs args)
        {
            try
            {
                await RefreshAllProjects();
            }
            catch (TaskCanceledException)
            {
                // Ignore.
            }
            catch (Exception e)
            {
                this.serviceProvider
                    .GetService<IExceptionDialog>()
                    .Show(this, "Refreshing projects failed", e);
            }
        }

        private async void addButton_Click(object sender, EventArgs args)
        {
            try
            {
                await AddProjectAsync();
            }
            catch (TaskCanceledException)
            {
                // Ignore.
            }
            catch (Exception e)
            {
                this.serviceProvider
                    .GetService<IExceptionDialog>()
                    .Show(this, "Adding project failed", e);
            }
        }

        private void openSettingsButton_Click(object sender, EventArgs _)
        {
            if (this.treeView.SelectedNode is InventoryNode inventoryNode)
            {
                this.serviceProvider
                    .GetService<ISettingsEditor>()
                    .ShowWindow(inventoryNode);
            }
        }

        private async void generateCredentialsToolStripButton_Click(object sender, EventArgs _)
        {
            try
            {
                if (this.treeView.SelectedNode is VmInstanceNode vmNode)
                {
                    await GenerateCredentials(vmNode);
                }
            }
            catch (TaskCanceledException)
            {
                // Ignore.
            }
            catch (Exception e)
            {
                this.serviceProvider
                    .GetService<IExceptionDialog>()
                    .Show(this, "Generating credentials failed", e);
            }
        }

        private async void connectToolStripButton_Click(object sender, EventArgs _)
        {
            try
            {
                if (this.treeView.SelectedNode is VmInstanceNode vmNode)
                {
                    await ConnectInstance(vmNode);
                }
            }
            catch (TaskCanceledException)
            {
                // Ignore.
            }
            catch (Exception e)
            {
                this.serviceProvider
                    .GetService<IExceptionDialog>()
                    .Show(this, "Generating credentials failed", e);
            }
        }


        //---------------------------------------------------------------------
        // Other Windows event handlers.
        //---------------------------------------------------------------------

        private async void ProjectExplorerWindow_Shown(object sender, EventArgs _)
        {
            try
            {
                await RefreshAllProjects();
            }
            catch (TaskCanceledException)
            {
                // Most likely, the user rejected to reauthorize. Quit the app.
                this.mainForm.Close();

            }
            catch (Exception e)
            {
                this.serviceProvider
                    .GetService<IExceptionDialog>()
                    .Show(this, "Loading projects failed", e);

                // Do not close the application, otherwise the user has no 
                // chance to remediate the situation by unloading the offending
                // project.
            }
        }

        private async void treeView_AfterSelect(object sender, TreeViewEventArgs args)
        {
            try
            {
                var selectedNode = (IProjectExplorerNode)args.Node;

                //
                // Update toolbar state.
                //
                this.openSettingsButton.Enabled = (args.Node is InventoryNode);
                this.generateCredentialsToolStripButton.Enabled = (selectedNode is VmInstanceNode);
                this.connectToolStripButton.Enabled = 
                    (selectedNode is VmInstanceNode) && ((VmInstanceNode)selectedNode).IsRunning;

                //
                // Update context menu state.
                //
                this.refreshToolStripMenuItem.Visible =
                this.unloadProjectToolStripMenuItem.Visible = (selectedNode is ProjectNode);
                this.refreshAllProjectsToolStripMenuItem.Visible = (selectedNode is CloudNode);
                this.propertiesToolStripMenuItem.Visible = (selectedNode is InventoryNode);

                this.cloudConsoleSeparatorToolStripMenuItem.Visible =
                    this.openInCloudConsoleToolStripMenuItem.Visible =
                    this.openlogsToolStripMenuItem.Visible = (selectedNode is VmInstanceNode);

                this.iapSeparatorToolStripMenuItem.Visible =
                    this.configureIapAccessToolStripMenuItem.Visible =
                         (selectedNode is VmInstanceNode || selectedNode is ProjectNode);

                this.generateCredentialsToolStripMenuItem.Visible = (selectedNode is VmInstanceNode);
                this.generateCredentialsToolStripMenuItem.Enabled =
                    this.connectToolStripMenuItem.Enabled =
                        (selectedNode is VmInstanceNode) && ((VmInstanceNode)selectedNode).IsRunning;

                //
                // Fire event.
                //
                await this.eventService.FireAsync(new ProjectExplorerNodeSelectedEvent(selectedNode));
            }
            catch (TaskCanceledException)
            {
                // Ignore.
            }
            catch (Exception e)
            {
                this.serviceProvider
                    .GetService<IExceptionDialog>()
                    .Show(this, "An error occured", e);
            }
        }
        private void ProjectExplorerWindow_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F4)
            {
                openSettingsButton_Click(sender, EventArgs.Empty);
            }
        }

        //---------------------------------------------------------------------
        // Service event handlers.
        //---------------------------------------------------------------------

        private async Task OnProjectAdded(ProjectInventoryService.ProjectAddedEvent e)
        {
            Debug.Assert(!this.InvokeRequired);

            await RefreshProject(e.ProjectId);
        }

        private void OnProjectDeleted(ProjectInventoryService.ProjectDeletedEvent e)
        {
            Debug.Assert(!this.InvokeRequired);
            var node = this.rootNode.Nodes
                .Cast<ProjectNode>()
                .Where(p => p.ProjectId == e.ProjectId)
                .FirstOrDefault();

            if (node != null)
            {
                // Remove corresponding node from tree.
                this.rootNode.Nodes.Remove(node);
            }
        }

        //---------------------------------------------------------------------
        // IProjectExplorer.
        //---------------------------------------------------------------------

        public void ShowWindow()
        {
            Show(this.dockPanel, DockState.DockLeft);
        }

        public async Task RefreshAllProjects()
        {
            Debug.Assert(!this.InvokeRequired);

            // Move selection to a "safe" spot.
            this.treeView.SelectedNode = this.rootNode;

            using (var computeEngineAdapter = this.serviceProvider.GetService<IComputeEngineAdapter>())
            {

                var failedProjects = new Dictionary<string, Exception>();

                var projectsAndInstances = await this.jobService.RunInBackground(
                    new JobDescription("Loading projects..."),
                    async token =>
                    {
                        var accumulator = new Dictionary<string, IEnumerable<Instance>>();

                        foreach (var project in await this.projectInventoryService.ListProjectsAsync())
                        {
                            try
                            {
                                accumulator[project.Name] =
                                    await computeEngineAdapter.QueryInstancesAsync(project.Name);
                            }
                            catch (Exception e)
                            {
                                // If one project fails to load, we should stil load the other onces.
                                failedProjects[project.Name] = e;
                            }
                        }

                        return accumulator;
                    });

                foreach (var entry in projectsAndInstances)
                {
                    PopulateProjectNode(entry.Key, entry.Value);
                }

                if (failedProjects.Any())
                {
                    // Add an (empty) project node so that the user can at least unload the project.
                    foreach (string projectId in failedProjects.Keys)
                    {
                        PopulateProjectNode(projectId, Enumerable.Empty<Instance>());
                    }

                    throw new AggregateException(
                        $"The following projects failed to refresh: {string.Join(", ", failedProjects.Keys)}",
                        failedProjects.Values.Cast<Exception>());
                }
            }
        }

        public async Task RefreshProject(string projectId)
        {
            Debug.Assert(!this.InvokeRequired);

            using (var computeEngineAdapter = this.serviceProvider.GetService<IComputeEngineAdapter>())
            {
                var instances = await this.jobService.RunInBackground(
                    new JobDescription("Loading project inventory..."),
                    token => computeEngineAdapter.QueryInstancesAsync(projectId));

                PopulateProjectNode(projectId, instances);
            }
        }

        public async Task ShowAddProjectDialogAsync()
        {
            ShowWindow();

            await AddProjectAsync();
        }
    }
}

