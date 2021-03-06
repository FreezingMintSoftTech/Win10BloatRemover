﻿using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Management.Automation;
using Win10BloatRemover.Utils;
using Env = System.Environment;

namespace Win10BloatRemover.Operations
{
    public enum UWPAppRemovalMode
    {
        KeepProvisionedPackages,
        RemoveProvisionedPackages
    }

    public enum UWPAppGroup
    {
        AlarmsAndClock,
        Bing,               // Weather, News, Finance and Sports
        Calculator,
        Camera,
        CommunicationsApps,
        Edge,
        HelpAndFeedback,
        Maps,
        Messaging,
        MixedReality,       // 3D Viewer, Print 3D and Mixed Reality Portal
        Mobile,             // YourPhone, OneConnect (aka Mobile plans) and Connect app
        OfficeHub,
        OneNote,
        Paint3D,
        Photos,
        SecurityCenter,
        Skype,
        SnipAndSketch,
        SolitaireCollection,
        StickyNotes,
        Store,
        Xbox,
        Zune                // Groove Music and Movies
    }

    public class UWPAppRemover : IOperation
    {
        // This dictionary contains the exact apps names corresponding to every defined group
        private static readonly Dictionary<UWPAppGroup, string[]> appNamesForGroup = new Dictionary<UWPAppGroup, string[]> {
            { UWPAppGroup.AlarmsAndClock, new[] { "Microsoft.WindowsAlarms" } },
            {
                UWPAppGroup.Bing, new[] {
                    "Microsoft.BingNews",
                    "Microsoft.BingWeather",
                    "Microsoft.BingFinance",
                    "Microsoft.BingSports"
                }
            },
            { UWPAppGroup.Calculator, new[] { "Microsoft.WindowsCalculator" } },
            { UWPAppGroup.Camera, new[] { "Microsoft.WindowsCamera" } },
            { UWPAppGroup.CommunicationsApps, new[] { "microsoft.windowscommunicationsapps", "Microsoft.People" } },
            { UWPAppGroup.Edge, new[] { "Microsoft.MicrosoftEdge", "Microsoft.MicrosoftEdgeDevToolsClient" } },
            {
                UWPAppGroup.HelpAndFeedback, new[] {
                    "Microsoft.WindowsFeedbackHub",
                    "Microsoft.GetHelp",
                    "Microsoft.Getstarted"
                }
            },
            { UWPAppGroup.Maps, new[] { "Microsoft.WindowsMaps" } },
            { UWPAppGroup.Messaging, new[] { "Microsoft.Messaging" } },
            {
                UWPAppGroup.MixedReality, new[] {
                    "Microsoft.Microsoft3DViewer",
                    "Microsoft.Print3D",
                    "Microsoft.MixedReality.Portal"
                }
            },
            { UWPAppGroup.Mobile, new[] { "Microsoft.YourPhone", "Microsoft.OneConnect", "Microsoft.PPIProjection" } },
            { UWPAppGroup.OfficeHub, new[] { "Microsoft.MicrosoftOfficeHub" } },
            { UWPAppGroup.OneNote, new[] { "Microsoft.Office.OneNote" } },
            { UWPAppGroup.Paint3D, new[] { "Microsoft.MSPaint" } },
            { UWPAppGroup.Photos, new[] { "Microsoft.Windows.Photos" } },
            { UWPAppGroup.SecurityCenter, new[] { "Microsoft.Windows.SecHealthUI" } },
            { UWPAppGroup.Skype, new[] { "Microsoft.SkypeApp" } },
            { UWPAppGroup.SnipAndSketch, new[] { "Microsoft.SkreenSketch" } },
            { UWPAppGroup.SolitaireCollection, new[] { "Microsoft.MicrosoftSolitaireCollection" } },
            { UWPAppGroup.StickyNotes, new[] { "Microsoft.MicrosoftStickyNotes" } },
            {
                UWPAppGroup.Store, new[] {
                    "Microsoft.WindowsStore",
                    "Microsoft.StorePurchaseApp",
                    "Microsoft.Services.Store.Engagement",
                }
            },
            {
                UWPAppGroup.Xbox, new[] {
                    "Microsoft.XboxGameCallableUI",
                    "Microsoft.XboxSpeechToTextOverlay",
                    "Microsoft.XboxApp",
                    "Microsoft.XboxGameOverlay",
                    "Microsoft.XboxGamingOverlay",
                    "Microsoft.XboxIdentityProvider",
                    "Microsoft.Xbox.TCUI"
                }
            },
            { UWPAppGroup.Zune, new[] {"Microsoft.ZuneMusic", "Microsoft.ZuneVideo" } }
        };

        private readonly Dictionary<UWPAppGroup, Action> postUninstallOperationsForGroup;
        private readonly UWPAppGroup[] appsToRemove;
        private readonly UWPAppRemovalMode removalMode;
        private readonly InstallWimTweak installWimTweak;
        private readonly IUserInterface ui;
        private /*lateinit*/ PowerShell psInstance;

        #nullable disable warnings
        public UWPAppRemover(UWPAppGroup[] appsToRemove, UWPAppRemovalMode removalMode, IUserInterface ui, InstallWimTweak installWimTweak)
        {
            this.appsToRemove = appsToRemove;
            this.removalMode = removalMode;
            this.ui = ui;
            this.installWimTweak = installWimTweak;

            postUninstallOperationsForGroup = new Dictionary<UWPAppGroup, Action> {
                { UWPAppGroup.CommunicationsApps, RemoveSyncHostService },
                { UWPAppGroup.Edge, RemoveEdgeResidualFiles },
                { UWPAppGroup.Mobile, RemoveConnectApp },
                { UWPAppGroup.Maps, RemoveMapsServicesAndTasks },
                { UWPAppGroup.Messaging, RemoveMessagingService },
                { UWPAppGroup.Paint3D, RemovePaint3DContextMenuEntries },
                { UWPAppGroup.Photos, RestoreWindowsPhotoViewer },
                { UWPAppGroup.MixedReality, RemoveMixedRealityAppsLeftovers },
                { UWPAppGroup.Xbox, RemoveXboxServicesAndTasks },
                { UWPAppGroup.Store, DisableStoreFeaturesAndServices }
            };
        }
        #nullable restore warnings

        public void Run()
        {
            using (psInstance = PowerShellExtensions.CreateWithImportedModules("AppX"))
            {
                foreach (UWPAppGroup appGroup in appsToRemove)
                {
                    bool atLeastOneAppUninstalled = UninstallAppsOfGroup(appGroup);

                    // We check if at least one app has been uninstalled to avoid
                    // performing tasks when no app of the group are installed anymore
                    if (atLeastOneAppUninstalled && psInstance.Streams.Error.Count == 0)
                        TryPerformPostUninstallOperations(appGroup);
                }
            }
        }

        private bool UninstallAppsOfGroup(UWPAppGroup appGroup)
        {
            ui.PrintHeading($"\nRemoving {appGroup} app(s)...");

            bool atLeastOneAppUninstalled = false;
            foreach (string appName in appNamesForGroup[appGroup])
            {
                UninstallApp(appName);
                if (!atLeastOneAppUninstalled)
                    atLeastOneAppUninstalled = psInstance.GetVariable("package").IsNotEmpty();
            }

            return atLeastOneAppUninstalled;
        }

        private void UninstallApp(string appName)
        {
            string appRemovalScript =
                @"$package = Get-AppxPackage -AllUsers -Name """ + appName + @""";
                if ($package) {
                    Write-Host ""Removing app " + appName + @"..."";
                    $package | Remove-AppxPackage -AllUsers;
                }
                else {
                    Write-Host ""App " + appName + @" is not installed."";
                }";

            if (removalMode == UWPAppRemovalMode.RemoveProvisionedPackages)
            {
                appRemovalScript +=
                    @"$provisionedPackage = Get-AppxProvisionedPackage -Online | where {$_.DisplayName -eq """ + appName + @"""};
                    if ($provisionedPackage) {
                        Write-Host ""Removing provisioned package for app " + appName + @"..."";
                        Remove-AppxProvisionedPackage -Online -PackageName $provisionedPackage.PackageName;
                    }
                    else {
                        Write-Host ""No provisioned package found for app " + appName + @"."";
                    }";
            }

            psInstance.RunScriptAndPrintOutput(appRemovalScript, ui);
        }

        private void TryPerformPostUninstallOperations(UWPAppGroup appGroup)
        {
            ui.PrintSubHeading($"\nPerforming post-uninstall operations for app {appGroup}...");
            try
            {
                PerformPostUninstallOperations(appGroup);
            }
            catch (Exception exc)
            {
                ui.PrintError($"Unable to complete post-uninstall operations for app group {appGroup}: {exc.Message}");
            }
        }

        /*
         * Removes any eventual services, scheduled tasks and/or registry keys related to the specified app group
         */
        private void PerformPostUninstallOperations(UWPAppGroup appGroup)
        {
            if (postUninstallOperationsForGroup.ContainsKey(appGroup))
                postUninstallOperationsForGroup[appGroup]();
            else
                Console.WriteLine("Nothing to do.");
        }

        private void RemoveEdgeResidualFiles()
        {
            Console.WriteLine("Removing old files...");
            SystemUtils.TryDeleteDirectoryIfExists(
                $@"{Env.GetFolderPath(Env.SpecialFolder.UserProfile)}\MicrosoftEdgeBackups",
                ui
            );
            SystemUtils.TryDeleteDirectoryIfExists(
                $@"{Env.GetFolderPath(Env.SpecialFolder.LocalApplicationData)}\MicrosoftEdge",
                ui
            );
        }

        private void RemoveConnectApp()
        {
            installWimTweak.RemoveComponentIfAllowed("Microsoft-PPIProjection-Package", ui);
        }

        private void RemoveMapsServicesAndTasks()
        {
            Console.WriteLine("Removing app-related scheduled tasks and services...");
            new ScheduledTasksDisabler(new[] {
                @"\Microsoft\Windows\Maps\MapsUpdateTask",
                @"\Microsoft\Windows\Maps\MapsToastTask"
            }, ui).Run();

            ServiceRemover.BackupAndRemove(new[] { "MapsBroker", "lfsvc" }, ui);
        }

        private void RemoveXboxServicesAndTasks()
        {
            Console.WriteLine("Removing app-related scheduled tasks and services...");
            new ScheduledTasksDisabler(new[] { @"Microsoft\XblGameSave\XblGameSaveTask" }, ui).Run();

            using RegistryKey key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\GameDVR");
            key.SetValue("AllowGameDVR", 0, RegistryValueKind.DWord);

            ServiceRemover.BackupAndRemove(new[] { "XblAuthManager", "XblGameSave", "XboxNetApiSvc", "XboxGipSvc" }, ui);
        }

        private void RemoveMessagingService()
        {
            Console.WriteLine("Removing app-related services...");
            ServiceRemover.BackupAndRemove(new[] { "MessagingService" }, ui);
        }

        private void RemovePaint3DContextMenuEntries()
        {
            Console.WriteLine("Removing Paint 3D context menu entries...");
            SystemUtils.ExecuteWindowsPromptCommand(
                @"echo off & for /f ""tokens=1* delims="" %I in " +
                 @"(' reg query ""HKEY_CLASSES_ROOT\SystemFileAssociations"" /s /k /f ""3D Edit"" ^| find /i ""3D Edit"" ') " +
                @"do (reg delete ""%I"" /f )",
                ui
            );
        }

        private void RemoveMixedRealityAppsLeftovers()
        {
            Remove3DObjectsFolder();
            Remove3DPrintContextMenuEntries();
        }

        private void Remove3DObjectsFolder()
        {
            Console.WriteLine("Removing 3D Objects folder...");
            using RegistryKey localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey key = localMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace", writable: true
            );
            key.DeleteSubKeyTree("{0DB7E03F-FC29-4DC6-9020-FF41B59E513A}", throwOnMissingSubKey: false);

            SystemUtils.TryDeleteDirectoryIfExists($@"{Env.GetFolderPath(Env.SpecialFolder.UserProfile)}\3D Objects", ui);
        }

        private void Remove3DPrintContextMenuEntries()
        {
            Console.WriteLine("Removing 3D Print context menu entries...");
            SystemUtils.ExecuteWindowsPromptCommand(
                @"echo off & for /f ""tokens=1* delims="" %I in " +
                @"(' reg query ""HKEY_CLASSES_ROOT\SystemFileAssociations"" /s /k /f ""3D Print"" ^| find /i ""3D Print"" ') " +
                @"do (reg delete ""%I"" /f )",
                ui
            );
        }

        private void RestoreWindowsPhotoViewer()
        {
            Console.WriteLine("Setting file association with original photo viewer for BMP, GIF, JPEG, PNG and TIFF pictures...");

            const string PHOTO_VIEWER_SHELL_COMMAND =
                @"%SystemRoot%\System32\rundll32.exe ""%ProgramFiles%\Windows Photo Viewer\PhotoViewer.dll"", ImageView_Fullscreen %1";
            const string PHOTO_VIEWER_CLSID = "{FFE2A43C-56B9-4bf5-9A79-CC6D4285608A}";

            using (RegistryKey key = Registry.ClassesRoot.CreateSubKey(@"Applications\photoviewer.dll\shell\open"))
                key.SetValue("MuiVerb", "@photoviewer.dll,-3043", RegistryValueKind.String);
            using (RegistryKey key = Registry.ClassesRoot.CreateSubKey(@"Applications\photoviewer.dll\shell\open\command"))
                key.SetValue("(Default)", PHOTO_VIEWER_SHELL_COMMAND, RegistryValueKind.ExpandString);
            using (RegistryKey key = Registry.ClassesRoot.CreateSubKey(@"Applications\photoviewer.dll\shell\open\DropTarget"))
                key.SetValue("Clsid", PHOTO_VIEWER_CLSID, RegistryValueKind.String);

            string[] imageTypes = { "Paint.Picture", "giffile", "jpegfile", "pngfile" };
            foreach (string type in imageTypes)
            {
                using (RegistryKey key = Registry.ClassesRoot.CreateSubKey($@"{type}\shell\open\command"))
                    key.SetValue("(Default)", PHOTO_VIEWER_SHELL_COMMAND, RegistryValueKind.ExpandString);
                using (RegistryKey key = Registry.ClassesRoot.CreateSubKey($@"{type}\shell\open\DropTarget"))
                    key.SetValue("Clsid", PHOTO_VIEWER_CLSID, RegistryValueKind.String);
            }
        }

        private void RemoveSyncHostService()
        {
            Console.WriteLine("Removing sync host service...");
            ServiceRemover.BackupAndRemove(new[] { "OneSyncSvc" }, ui);
        }

        private void DisableStoreFeaturesAndServices()
        {
            Console.WriteLine("Writing values into the Registry...");
            using (RegistryKey key = Registry.LocalMachine.CreateSubKey(@"Software\Policies\Microsoft\WindowsStore"))
            {
                key.SetValue("RemoveWindowsStore", 1, RegistryValueKind.DWord);
                key.SetValue("DisableStoreApps", 1, RegistryValueKind.DWord);
            }
            using (RegistryKey key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\PushToInstall"))
                key.SetValue("DisablePushToInstall", 1, RegistryValueKind.DWord);

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager"))
                key.SetValue("SilentInstalledAppsEnabled", 0, RegistryValueKind.DWord);
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\AppHost"))
                key.SetValue("EnableWebContentEvaluation", 0, RegistryValueKind.DWord);

            Console.WriteLine("Removing app-related services...");
            ServiceRemover.BackupAndRemove(new[] { "PushToInstall" }, ui);
        }
    }
}
