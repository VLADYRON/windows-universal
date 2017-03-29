﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Microsoft.Practices.Unity;
using Prism.Unity.Windows;
using Prism.Windows.AppModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.Resources;
using Windows.Foundation;
using Windows.Security.Credentials;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.Storage.Pickers.Provider;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Newtonsoft.Json;
using NextcloudApp.Models;
using NextcloudApp.Services;
using NextcloudApp.Utils;
using NextcloudClient.Exceptions;
using NextcloudClient.Types;
using Prism.Windows.Mvvm;

namespace NextcloudApp
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    sealed partial class App : PrismUnityApplication
    {
        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            UnhandledException += OnUnhandledException;
            TaskScheduler.UnobservedTaskException += TaskSchedulerOnUnobservedTaskException;
            InitializeComponent();
        }

        private async void TaskSchedulerOnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs args)
        {
            var exceptionStackTrace = args.Exception.StackTrace;
            var
                exceptionHashCode = string.IsNullOrEmpty(exceptionStackTrace)
                    ? args.Exception.GetHashCode().ToString()
                    : exceptionStackTrace.GetHashCode().ToString();
            await
                ExceptionReportService.Handle(args.Exception.GetType().ToString(), args.Exception.Message,
                    exceptionStackTrace, args.Exception.InnerException.GetType().ToString(), exceptionHashCode);
        }

        private async void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
        {
            var exceptionStackTrace = string.Empty;
            try
            {
                exceptionStackTrace = args.Exception.StackTrace + "";
            }
            // ReSharper disable once EmptyGeneralCatchClause
            catch { }

            var exceptionMessage = args.Message;
            var exceptionType = string.Empty;
            var innerExceptionType = string.Empty;
            var exceptionHashCode = string.Empty;

            if (args.Exception != null)
            {
                // Tasks will throw a canceled exception if they get canceled
                // We don't care, but avoid closing the app
                if (args.Exception.GetType() == typeof(TaskCanceledException))
                {
                    args.Handled = true;
                    return;
                }
                if (args.Exception.GetType() == typeof(OperationCanceledException))
                {
                    args.Handled = true;
                    return;
                }
                if (args.Exception.GetType() == typeof(FileNotFoundException))
                {
                    args.Handled = true;
                    return;
                }
                // Temporary Workaround for WP10
                if (args.Exception.GetType() == typeof(ArgumentException))
                {
                    args.Handled = true;
                    return;
                }
                if (args.Exception.GetType() == typeof(ResponseError))
                {
                    args.Handled = true;
                    ResponseErrorHandlerService.HandleException((ResponseError)args.Exception);
                    return;
                }

                // 0x8000000B, E_BOUNDS, System.Exception, OutOfBoundsException
                if ((uint)args.Exception.HResult == 0x80004004)
                {
                    args.Handled = true;
                    return;
                }

                // 0x80072EE7, ERROR_WINHTTP_NAME_NOT_RESOLVED, The server name or address could not be resolved
                if ((uint)args.Exception.HResult == 0x80072EE7)
                {
                    args.Handled = true;
                    var resourceLoader = Container.Resolve<IResourceLoader>();
                    var dialogService = Container.Resolve<DialogService>();
                    var dialog = new ContentDialog
                    {
                        Title = resourceLoader.GetString("AnErrorHasOccurred"),
                        Content = new TextBlock
                        {
                            Text = resourceLoader.GetString("ServerNameOrAddressCouldNotBeResolved"),
                            TextWrapping = TextWrapping.WrapWholeWords,
                            Margin = new Thickness(0, 20, 0, 0)
                        },
                        PrimaryButtonText = resourceLoader.GetString("OK")
                    };
                    await dialogService.ShowAsync(dialog);
                    return;
                }

                exceptionType = args.Exception.GetType().ToString();
                if (args.Exception.InnerException != null)
                {
                    innerExceptionType = args.Exception.InnerException.GetType().ToString();
                }
                exceptionHashCode = string.IsNullOrEmpty(exceptionStackTrace)
                    ? args.Exception.GetHashCode().ToString()
                    : exceptionStackTrace.GetHashCode().ToString();
            }

            if (args.Handled)
            {
                return;
            }
            args.Handled = true;
            await
                ExceptionReportService.Handle(exceptionType, exceptionMessage, exceptionStackTrace,
                    innerExceptionType, exceptionHashCode);
        }

        protected override UIElement CreateShell(Frame rootFrame)
        {
            var shell = Container.Resolve<AppShell>();
            shell.SetContentFrame(rootFrame);
            return shell;
        }

        protected override void OnShareTargetActivated(ShareTargetActivatedEventArgs args)
        {
            OnActivated(args);
        }

        protected override void OnFileSavePickerActivated(FileSavePickerActivatedEventArgs args)
        {
            OnActivated(args);
        }

        protected override void OnFileActivated(FileActivatedEventArgs args)
        {
            OnActivated(args);
        }

        protected override async Task OnActivateApplicationAsync(IActivatedEventArgs args)
        {
            await base.OnActivateApplicationAsync(args);
            if (args.Kind == ActivationKind.ShareTarget)
            {
                var activatedEventArgs = args as ShareTargetActivatedEventArgs;
                if (activatedEventArgs != null)
                {
                    var sorageItems = await activatedEventArgs.ShareOperation.Data.GetStorageItemsAsync();

                    var pageParameters = new ShareTargetPageParameters()
                    {
                        //ShareOperation = activatedEventArgs.ShareOperation,
                        ActivationKind = ActivationKind.ShareTarget,
                        FileTokens = new List<string>()
                    };

                    StorageApplicationPermissions.FutureAccessList.Clear();
                    foreach (var storageItem in sorageItems)
                    {
                        var token = StorageApplicationPermissions.FutureAccessList.Add(storageItem);
                        pageParameters.FileTokens.Add(token);
                    }

                    activatedEventArgs.ShareOperation.ReportDataRetrieved();
                    CheckSettingsAndContinue(PageToken.ShareTarget, pageParameters);
                }
            }
            else if (args.Kind == ActivationKind.FileSavePicker)
            {
                var activatedEventArgs = args as FileSavePickerActivatedEventArgs;
                if (activatedEventArgs != null)
                {
                    activatedEventArgs.FileSavePickerUI.TargetFileRequested += OnTargetFileRequested;
                }
            }
            else if (args.Kind == ActivationKind.File)
            {
                var activatedEventArgs = args as FileActivatedEventArgs;
                if (activatedEventArgs != null)
                {
                    var sorageItems = activatedEventArgs.Files;

                    var pageParameters = new ShareTargetPageParameters()
                    {
                        //ShareOperation = activatedEventArgs.ShareOperation,
                        ActivationKind = ActivationKind.ShareTarget,
                        FileTokens = new List<string>()
                    };

                    StorageApplicationPermissions.FutureAccessList.Clear();
                    foreach (var storageItem in sorageItems)
                    {
                        var token = StorageApplicationPermissions.FutureAccessList.Add(storageItem);
                        pageParameters.FileTokens.Add(token);
                    }

                    CheckSettingsAndContinue(PageToken.ShareTarget, pageParameters);
                }
            }
        }

        private void OnTargetFileRequested(FileSavePickerUI sender, TargetFileRequestedEventArgs args)
        {
            // Requesting a deferral allows the app to call another asynchronous method and complete the request at a later time 
            var deferral = args.Request.GetDeferral();

            //args.Request.TargetFile = await ApplicationData.Current.LocalFolder.CreateFileAsync(sender.FileName, CreationCollisionOption.GenerateUniqueName);

            // Complete the deferral to let the Picker know the request is finished 
            deferral.Complete();
        }

        protected override Task OnInitializeAsync(IActivatedEventArgs args)
        {
            Container.RegisterInstance(new DialogService());
            Container.RegisterInstance<IResourceLoader>(new ResourceLoaderAdapter(new ResourceLoader()));

            var task = base.OnInitializeAsync(args);

            DeviceGestureService.GoBackRequested += DeviceGestureServiceOnGoBackRequested;

            // Just count total app starts
            SettingsService.Instance.LocalSettings.AppTotalRuns = SettingsService.Instance.LocalSettings.AppTotalRuns + 1;

            // Count app starts after last update
            var currentVersion =
                $"{Package.Current.Id.Version.Major}.{Package.Current.Id.Version.Minor}.{Package.Current.Id.Version.Build}.{Package.Current.Id.Version.Revision}";
            if (currentVersion == SettingsService.Instance.LocalSettings.AppRunsAfterLastUpdateVersion)
            {
                SettingsService.Instance.LocalSettings.AppRunsAfterLastUpdate = SettingsService.Instance.LocalSettings.AppRunsAfterLastUpdate + 1;
            }
            else
            {
                SettingsService.Instance.LocalSettings.AppRunsAfterLastUpdateVersion = currentVersion;
                SettingsService.Instance.LocalSettings.AppRunsAfterLastUpdate = 1;
                SettingsService.Instance.LocalSettings.ShowUpdateMessage = true;
            }

            MigrationService.Instance.StartMigration();

            return task;
        }
        
        protected override Task OnLaunchApplicationAsync(LaunchActivatedEventArgs args)
        {
            // Ensure the current window is active
            Window.Current.Activate();

            PinStartPageParameters pageParameters = null;
            if (!string.IsNullOrEmpty(args?.Arguments))
            {
                var tmpResourceInfo = JsonConvert.DeserializeObject<ResourceInfo>(args.Arguments);
                if (tmpResourceInfo != null)
                {
                    pageParameters = new PinStartPageParameters()
                    {
                        ResourceInfo = tmpResourceInfo,
                        PageTarget = tmpResourceInfo.IsDirectory() ? PageToken.DirectoryList : PageToken.FileInfo
                    };
                }
            }

            if (SettingsService.Instance.LocalSettings.UseWindowsHello)
            {
                CheckSettingsAndContinue(PageToken.Verification, pageParameters);
            }
            else
            {
                CheckSettingsAndContinue(pageParameters?.PageTarget ?? PageToken.DirectoryList, pageParameters);
            }

            return Task.FromResult(true);
        }

        private void CheckSettingsAndContinue(PageToken requestedPage, IPageParameters pageParameters)
        {
            if (
                string.IsNullOrEmpty(SettingsService.Instance.LocalSettings.ServerAddress) ||
                string.IsNullOrEmpty(SettingsService.Instance.LocalSettings.Username)
            )
            {
                NavigationService.Navigate(PageToken.Login.ToString(), null);
            }
            else
            {
                var vault = new PasswordVault();

                IReadOnlyList<PasswordCredential> credentialList = null;
                try
                {
                    credentialList = vault.FindAllByResource(SettingsService.Instance.LocalSettings.ServerAddress);
                }
                catch
                {
                    // ignored
                }

                var credential = credentialList?.FirstOrDefault(item => item.UserName.Equals(SettingsService.Instance.LocalSettings.Username));

                if (credential != null)
                {
                    credential.RetrievePassword();
                    if (!string.IsNullOrEmpty(credential.Password))
                    {
                        NavigationService.Navigate(requestedPage.ToString(), pageParameters?.Serialize());
                    }
                    else
                    {
                        NavigationService.Navigate(
                            PageToken.Login.ToString(),
                            null);
                    }
                }
                else
                {
                    NavigationService.Navigate(
                        PageToken.Login.ToString(),
                        null);
                }
            }
        }

        private void DeviceGestureServiceOnGoBackRequested(object sender, DeviceGestureEventArgs e)
        {
            var appShell = (AppShell)Window.Current.Content;
            var contentFrame = (Frame)appShell.GetContentFrame();
            var page = (SessionStateAwarePage)contentFrame.Content;
            var revertable = page?.DataContext as IRevertState;
            if (revertable == null || !revertable.CanRevertState())
            {
                return;
            }
            e.Handled = true;
            e.Cancel = true;
            revertable.RevertState();
        }
    }
}
