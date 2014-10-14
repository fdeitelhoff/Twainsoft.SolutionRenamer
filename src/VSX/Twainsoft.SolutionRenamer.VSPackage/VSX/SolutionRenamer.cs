﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Runtime.InteropServices;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Twainsoft.SolutionRenamer.VSPackage.GUI;
using VSLangProj110;
using VSLangProj80;

namespace Twainsoft.SolutionRenamer.VSPackage.VSX
{
    [PackageRegistration(UseManagedResourcesOnly = true)]
    [InstalledProductRegistration("#110", "#112", "1.0", IconResourceID = 400)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [Guid(GuidList.SolutionRenamerVsPackagePkgString)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string)]
    public sealed class SolutionRenamer : Package
    {
        private RenameData RenameData { get; set; }

        protected override void Initialize()
        {
            base.Initialize();

            var mcs = GetService(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (mcs == null)
            {
                return;
            }

            var contextMenuCommandId = new CommandID(GuidList.SolutionRenamerVsPackageCmdSet, (int)PkgCmdIdList.ContextMenuCommandId);
            var contextMenu = new MenuCommand(OnRenameProject, contextMenuCommandId);
            mcs.AddCommand(contextMenu);

            // Data we need all the time during the rename process.
            RenameData = new RenameData();

            GetGlobalServices();

            ProjectsWithReferences = new List<Project>();
        }

        private void OnRenameProject(object sender, EventArgs e)
        {
            var progressDialog = new RenamingProgressDialog();


            try
            {
                // Get the currently selected project within the solution explorer.
                var currentProject = GetSelectedProject();

                // Get the new project name from the user.
                var rename = new RenameProjectDialog(currentProject.Name);
                var result = rename.ShowDialog();
                if (!result.HasValue || !result.Value)
                {
                    return;
                }

                // TODO: Check if there's another project with this name! (Where to check??)
                // This is the new project name the user typed in.
                RenameData.NewProjectName = rename.GetProjectName();

                // Check if this is necessary when the references check was refactored!
                ProjectsWithReferences.Clear();

                // Save all changes that were made before the renaming process. Just for safety!
                SaveSolution();

                // Check if there's an solution folder or return null.
                var solutionFolder = GetSolutionFolder(currentProject);

                // Get the file name and the parent directory of the current project before it gets renamed!
                var projectFileName = currentProject.Name;
                var projectParentDirectory = GetProjectParentDirectory(currentProject);

                // Check if the current project is the startup project before it gets renamed and temporarily deleted.
                var isStartupProject = IsStartupProject(currentProject);

                var oldProjectName = currentProject.Name;
                // This is a little bit scary: I need the old project path, before it gets moved. But this instance will have the new name after it gets renamed.
                // Change this behavior!
                OldProject = currentProject;
                OldProjectName = oldProjectName;

                // Rename the project. This changes the project filename too!
                currentProject.Name = RenameData.NewProjectName;

                // The hierarchy is needed for some of the following actions.
                IVsHierarchy currentProjectHierarchy;
                RenameData.Solution.GetProjectOfUniqueName(currentProject.UniqueName, out currentProjectHierarchy);

                if (projectFileName == projectParentDirectory)
                {
                    // Check if other projects have references to the currently selected project. These references must be changed too!
                    CheckProjectsForReferences();

                    // We need some data for future actions. Collect them here because the project is ready to get removed from the solution!
                    var newProjectFileName = Path.GetFileName(currentProject.FileName);
                    var fullProjectName = currentProject.FullName;
                    var newProjectDirectory = currentProject.Name;

                    // Remove the project from the solution file!
                    RemoveProjectFromSolution(currentProjectHierarchy);

                    // Move the project folder on the file system within the solution folder!
                    MoveProjectFolder(fullProjectName, newProjectDirectory);

                    // Add the renamed project to the solution. Either directly or within a solution folder.
                    // The return project is the new current project we're using for all other steps.
                    currentProject = AddProjectToSolution(solutionFolder, newProjectFileName, fullProjectName,
                        newProjectDirectory);

                    // Save the solution file after we moved the project.
                    SaveSolution();
                }

                // Change the reference of the renamed project within all other projects that had such a reference.
                ChangeRenamedProjectReferences(currentProject);

                // Change some project data like the default namespace and the assembly name. 
                ChangeProjectData(currentProject, oldProjectName, RenameData.NewProjectName);

                // Save the project after we made so many changes to it.
                SaveProject(currentProject, currentProjectHierarchy);

                // Change some data in the AssemblyInfo.cs file if those data matches the old project name! (AssemblyTitle and AssemblyProduct)
                ChangeAssemblyData(currentProject, oldProjectName, RenameData.NewProjectName, currentProjectHierarchy);

                // If the renamed project was the startup project, we need to refresh this setting after it was deleted.
                if (isStartupProject)
                {
                    RenameData.Dte.Solution.Properties.Item("StartupProject").Value = currentProject.Name;
                }

                // Rebuild the complete solution.
                RenameData.Dte.Solution.SolutionBuild.Build();
                // Better this way?
                //dte.Solution.SolutionBuild.BuildProject();
            }
            catch (COMException comException)
            {
                VsMessageBox.ShowErrorMessageBox("COMException", comException.ToString());
            }
            catch (IOException ioException)
            {
                VsMessageBox.ShowErrorMessageBox("IOException", ioException.ToString());
            }
            catch (Exception exception)
            {
                VsMessageBox.ShowErrorMessageBox("Unknown Exception", exception.ToString());
            }
            finally
            {
                progressDialog.Close();
            }
        }

        private void GetGlobalServices()
        {
            RenameData.Solution = GetGlobalService(typeof(IVsSolution)) as IVsSolution;
            RenameData.Dte = GetGlobalService(typeof(SDTE)) as DTE2;

            if (RenameData.Solution == null || RenameData.Dte == null)
            {
                throw new InvalidOperationException("The Solution Or The Dte Object is null!");
            }
        }

        private void SaveSolution()
        {
            StatusBarHelper.Update("Saving the current solution...");

            if (RenameData.Dte.Solution.IsDirty)
            {
                RenameData.Solution.SaveSolutionElement((uint)__VSSLNSAVEOPTIONS.SLNSAVEOPT_ForceSave, null, 0);
            }
        }

        private SolutionFolder GetSolutionFolder(Project project)
        {
            if (project.ParentProjectItem == null)
            {
                return null;
            }

            var parentProject = project.ParentProjectItem.Collection.Parent as Project;

            if (parentProject == null)
            {
                throw new InvalidOperationException("The Parent Project Of The Current Selected Project cannot be determined!");
            }

            return parentProject.Object as SolutionFolder;
        }
        
        private string GetProjectParentDirectory(Project project)
        {
            var parent = Directory.GetParent(project.FullName);

            if (parent == null)
            {
                throw new InvalidOperationException("The Project Parent Directory Is Null!");
            }

            return parent.Name;
        }

        private bool IsStartupProject(Project project)
        {
            return RenameData.Dte.Solution.Properties.Item("StartupProject").Value.ToString() == project.Name;
        }

        private Project GetSelectedProject()
        {
            IntPtr hierarchyPointer, selectionContainerPointer;
            object selectedObject = null;
            IVsMultiItemSelect multiItemSelect;
            uint projectItemId;

            var monitorSelection =
                (IVsMonitorSelection)GetGlobalService(
                    typeof(SVsShellMonitorSelection));

            monitorSelection.GetCurrentSelection(out hierarchyPointer,
                out projectItemId,
                out multiItemSelect,
                out selectionContainerPointer);

            var selectedHierarchy = Marshal.GetTypedObjectForIUnknown(
                hierarchyPointer, typeof (IVsHierarchy)) as IVsHierarchy;

            if (selectedHierarchy != null)
            {
                ErrorHandler.ThrowOnFailure(selectedHierarchy.GetProperty(
                    projectItemId,
                    (int)__VSHPROPID.VSHPROPID_ExtObject,
                    out selectedObject));
            }

            return selectedObject as Project;
        }

        // There are events for references added, removed and changed. Maybe this is useful in the future?
        private void CheckProjectsForReferences()
        {
            StatusBarHelper.Update("Checking other projects for references to the renamed one...");

            foreach (Project proj in RenameData.Dte.Solution.Projects)
            {
                    NavigateProject(proj);
            }
        }

        // Maybe this is doable in another way? This private field is used for data just for the navigate project code. Looks a little bit ugly.
        private Project OldProject;
        private string OldProjectName;
        private List<Project> ProjectsWithReferences;

        private void NavigateProject(Project project)
        {
            if (project.Name != RenameData.NewProjectName)
            {
                if (project.Kind == "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}")
                {
                    //Debug.WriteLine("Project: " + project.Name);

                    CheckProjectReferences(project);
                }

                // We need to navigate all found project items. There could be a solution folder with some projects within.
                NavigateProjectItems(project.ProjectItems);
            }
        }

        private void NavigateProjectItems(ProjectItems projectItems)
        {
            if (projectItems != null)
            {
                foreach (ProjectItem projectItem in projectItems)
                {
                    if (projectItem.SubProject != null)
                    {
                        NavigateProject(projectItem.SubProject);
                    }
                }
            }
        }

        private void CheckProjectReferences(Project proj)
        {
            var project = proj.Object as VSProject2;

            var references = project.References as References2;

            foreach (Reference5 reference in references)
            {
                // OldProjectName seems to be the new one!
                // The references path is the DLL in the debug folder. That cannot be compared easily.
                // Maybe SourceProject.FullName is better? Try it out in the next episode...
                if (reference.SourceProject != null && reference.Name == RenameData.NewProjectName && reference.SourceProject.FullName == OldProject.FullName)
                {
                    //Debug.WriteLine(reference.Name + " " + reference.Path);

                    ProjectsWithReferences.Add(proj);
                }
            }
        }

        private void RemoveProjectFromSolution(IVsHierarchy projectHierarchy)
        {
            StatusBarHelper.Update("Removing the old project from the solution...");

            RenameData.Solution.CloseSolutionElement(
                        (uint)__VSSLNSAVEOPTIONS.SLNSAVEOPT_ForceSave |
                        (uint)__VSSLNCLOSEOPTIONS.SLNCLOSEOPT_DeleteProject, projectHierarchy, 0);
        }
        
        private void MoveProjectFolder(string fullProjectName, string newProjectDirectory)
        {
            StatusBarHelper.Update("Moving the project within the file system...");

            var parentProjectDirectory = new DirectoryInfo(fullProjectName).Parent;

            if (parentProjectDirectory == null)
            {
                throw new InvalidOperationException("The Parent Project Directory Is Null!");
            }

            // Yes, my naming is... perfect?
            var parentProjectParentDirectory = parentProjectDirectory.Parent;

            if (parentProjectParentDirectory == null)
            {
                throw new InvalidOperationException("The Parent Project Parent Directory Is Null!");
            }

            // Move the current project folder to a folder with the new project name.
            parentProjectDirectory.MoveTo(Path.Combine(parentProjectParentDirectory.FullName, newProjectDirectory));
        }

        private Project AddProjectToSolution(SolutionFolder solutionFolder, string newProjectFileName, string fullProjectName, string newProjectDirectory)
        {
            StatusBarHelper.Update("Adding the new project to the solution...");

            var parentProjectDirectory = new DirectoryInfo(fullProjectName).Parent;

            if (parentProjectDirectory == null)
            {
                throw new InvalidOperationException("The Project Parent Directory Is Null!");
            }

            // Yes, my naming is... perfect?
            var parentProjectParentDirectory = parentProjectDirectory.Parent;

            if (parentProjectParentDirectory == null)
            {
                throw new InvalidOperationException("The Parent Project Parent Directory Is Null!");
            }

            // If there's no solution folder, we can add the project directory to the solution.
            if (solutionFolder == null)
            {
                return RenameData.Dte.Solution.AddFromFile(
                        Path.Combine(Path.Combine(parentProjectParentDirectory.FullName, newProjectDirectory), newProjectFileName));
            }

            // Otherwise we must add the renamed project to the solution folder.
            return solutionFolder.AddFromFile(
                        Path.Combine(Path.Combine(parentProjectParentDirectory.FullName, newProjectDirectory), newProjectFileName));
        }

        private void ChangeAssemblyData(Project currentProject, string oldProjectName, string newProjectName, IVsHierarchy currentProjectHierarchy)
        {
            StatusBarHelper.Update("Changing Assembly data...");

            var properties = currentProject.ProjectItems.Item("Properties");
            var assemblyInfo = properties.ProjectItems.Item("AssemblyInfo.cs");

            var assemblyTitle = assemblyInfo.FileCodeModel.CodeElements.Item("AssemblyTitle") as CodeAttribute2;
            var assemblyProduct = assemblyInfo.FileCodeModel.CodeElements.Item("AssemblyProduct") as CodeAttribute2;

            if (assemblyTitle == null || assemblyProduct == null)
            {
                throw new InvalidOperationException("AssemblyTitle Or AssemblyProduct Attribute Is Null!");
            }

            if (assemblyTitle.Value.Contains(oldProjectName))
            {
                assemblyTitle.Value = assemblyTitle.Value.Replace(oldProjectName, newProjectName);
            }

            if (assemblyProduct.Value.Contains(oldProjectName))
            {
                assemblyProduct.Value = assemblyProduct.Value.Replace(oldProjectName, newProjectName);
            }

            if (assemblyInfo.IsDirty)
            {
                RenameData.Solution.SaveSolutionElement((uint)__VSSLNSAVEOPTIONS.SLNSAVEOPT_ForceSave, currentProjectHierarchy, 0);
            }
        }

        private void ChangeRenamedProjectReferences(Project currentProject)
        {
            foreach (var proj in ProjectsWithReferences)
            {
                var project = proj.Object as VSProject2;

                if (project == null)
                {
                    continue;
                }

                var references = project.References as References2;

                if (references == null)
                {
                    continue;
                }

                references.AddProject(currentProject);
            }
        }

        private void ChangeProjectData(Project project, string oldProjectName, string newProjectName)
        {
            StatusBarHelper.Update("Changing project data...");

            var defaultNamespace = project.Properties.Item("DefaultNamespace");
            var assemblyName = project.Properties.Item("AssemblyName");

            if (defaultNamespace.Value.ToString().Contains(oldProjectName))
            {
                defaultNamespace.Value = defaultNamespace.Value.ToString()
                    .Replace(oldProjectName, newProjectName);
            }

            if (assemblyName.Value.ToString().Contains(oldProjectName))
            {
                assemblyName.Value = assemblyName.Value.ToString().Replace(oldProjectName, newProjectName);
            }
        }

        private void SaveProject(Project project, IVsHierarchy projectHierarchy)
        {
            StatusBarHelper.Update("Saving the renamed project...");

            if (project.IsDirty)
            {
                RenameData.Solution.SaveSolutionElement((uint)__VSSLNSAVEOPTIONS.SLNSAVEOPT_ForceSave, projectHierarchy, 0);
            }
        }
    }
}
