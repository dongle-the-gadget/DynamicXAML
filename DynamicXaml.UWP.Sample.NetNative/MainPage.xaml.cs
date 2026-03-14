using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Markup;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using DynamicXaml;
using DynamicXaml.UWP;
using MrmLib;
using DynamicXaml.UWP.Sample.NetNative.Polyfills;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace DynamicXaml.UWP.Sample.NetNative
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        public MainPage()
        {
            this.InitializeComponent();
        }

        private async void Page_Loaded(object sender, RoutedEventArgs e)
        {
            FileOpenPicker picker = new()
            {
                FileTypeFilter = { ".dll", ".pri", ".winmd" },
                CommitButtonText = "Load"
            };

        Pick:
            var result = await picker.PickMultipleFilesAsync();
            if (result is { Count: >= 2 } files)
            {
                var pri = files.FirstOrDefault(f => f.FileType == ".pri");
                var dll = files.FirstOrDefault(f => f.FileType == ".dll");
                var winmd = files.FirstOrDefault(f => f.FileType == ".winmd");

                if (pri is not null && dll is not null)
                {
                    var name = $"{dll.Name[0..^4]}.DynamicPage";

                    var appdata = ApplicationData.Current.LocalFolder;

                    nint pDllGetActivationFactory = 0;
                    dll = await dll.CopyAsync(appdata, dll.Name, NameCollisionOption.ReplaceExisting);
                    bool isNative = NativeLibrary.TryLoad(dll.Path, out nint handle) && NativeLibrary.TryGetExport(handle, "DllGetActivationFactory", out pDllGetActivationFactory);
                    if (!isNative)
                    {
                        MessageDialog msg = new("The .NET Native sample doesn't support loading managed dlls.", "Managed DLLs Selected");
                        await msg.ShowAsync();
                        goto Pick;
                    }

                    var box = new TextBox
                    {
                        Text = name,
                        PlaceholderText = "UIElement fully qualified class name (e.g. MyLibrary.Controls.MyPage)",
                        Margin = new(15)
                    };

                    var xamlReaderCheckbox = new CheckBox
                    {
                        Content = "Use XamlReader (needs WinMD in case of native dlls)",
                        Margin = new(15, 0, 15, 10),
                        IsChecked = false,
                        IsEnabled = winmd is not null,
                        HorizontalAlignment = HorizontalAlignment.Left
                    };

                    var mergePriResourcesCheckbox = new CheckBox
                    {
                        Content = "Embed resources referenced by path into the PRI (needed if the PRI was compiled without DisableEmbeddedXbf=false)",
                        Margin = new(15, 0, 15, 15),
                        IsChecked = false,
                        HorizontalAlignment = HorizontalAlignment.Left
                    };

                    var stack = new StackPanel()
                    {
                        Orientation = Orientation.Vertical,
                        Children =
                        {
                            box,
                            xamlReaderCheckbox,
                            mergePriResourcesCheckbox
                        }
                    };

                    var dialog = new ContentDialog
                    {
                        Title = "Enter UIElement fully qualified class name to load",
                        Content = stack,
                        PrimaryButtonText = "Load",
                        IsPrimaryButtonEnabled = true,
                        IsSecondaryButtonEnabled = false,
                        DefaultButton = ContentDialogButton.Primary,
                    };

                    dialog.PrimaryButtonClick += (s, e) =>
                    {
                        if (string.IsNullOrWhiteSpace(box.Text))
                        {
                            e.Cancel = true;
                        }
                    };

                    if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                    {
                        if (mergePriResourcesCheckbox.IsChecked is true)
                        {
                            var folderPicker = new FolderPicker
                            {
                                FileTypeFilter = { "*" },
                                CommitButtonText = "Select PRI root folder (usually has the same name as the PRI)"
                            };

                            if (await folderPicker.PickSingleFolderAsync() is { } rootFolder)
                            {
                                var priFile = await PriFile.LoadAsync(pri);
                                _ = await priFile.ReplacePathCandidatesWithEmbeddedDataAsync(rootFolder);

                                pri = await appdata.CreateFileAsync(pri.Name, CreationCollisionOption.ReplaceExisting);
                                await priFile.WriteAsync(pri);
                            }
                            else
                            {
                                MessageDialog errorDialog = new("Please select the PRI root folder or disable PRI resource embedding.", "No PRI Root Folder Selected");
                                await errorDialog.ShowAsync();
                                goto Pick;
                            }
                        }
                        else
                        {
                            pri = await pri.CopyAsync(appdata, pri.Name, NameCollisionOption.ReplaceExisting);
                        }

                        name = box.Text;
                        winmd = winmd is not null ? await winmd.CopyAsync(appdata, winmd.Name, NameCollisionOption.ReplaceExisting) : null;

                        DynamicLoader.LoadPri(pri);

                        UIElement element = null;
                        bool useXamlReader = xamlReaderCheckbox.IsChecked is true;

                        IReadOnlyList<string> providerTypeNames = null;
                        List<IXamlMetadataProvider> providers = null;

                        if (useXamlReader)
                        {
                            providerTypeNames = winmd is not null ? await XamlMetadataProviderHelper.GetProviderTypeNamesFromAssemblyAsync(winmd) : null;
                            useXamlReader = providerTypeNames is not null && providerTypeNames.Count > 0;
                        }

                        unsafe
                        {
                            var DllGetActivationFactory = (delegate* unmanaged[Stdcall]<void*, void**, int>)pDllGetActivationFactory;

                            if (useXamlReader)
                            {
                                providers = new();
                                foreach (var providerTypeName in providerTypeNames)
                                {
                                    void* factoryPtr = default;
                                    if (DllGetActivationFactory((void*)WindowsRuntimeMarshal.StringToHString(providerTypeName), &factoryPtr) >= 0)
                                    {
                                        void* obj = default;
                                        var vtftbl = *(void***)factoryPtr;
                                        var ActivateInstance = Marshal.GetDelegateForFunctionPointer<ActivateInstanceIXMPDelegate>((nint)vtftbl[6]);
                                        Marshal.ThrowExceptionForHR(ActivateInstance(factoryPtr, out var provider));
                                        providers.Add(provider);
                                    }
                                }
                            }
                            else
                            {
                                void* factoryPtr = default;
                                if (DllGetActivationFactory((void*)WindowsRuntimeMarshal.StringToHString(name), &factoryPtr) >= 0)
                                {
                                    var vtftbl = *(void***)factoryPtr;
                                    var ActivateInstance = Marshal.GetDelegateForFunctionPointer<ActivateInstanceElementDelegate>((nint)vtftbl[6]);
                                    Marshal.ThrowExceptionForHR(ActivateInstance(factoryPtr, out element));
                                }
                            }
                        }

                        if (useXamlReader && providers is not null)
                        {
                            foreach (var provider in providers)
                            {
                                _ = DynamicLoader.RegisterXamlMetadataProvider(provider);
                            }

                            var shortName = name.Split('.').Last();
                            var namespaceName = name[0..^(shortName.Length + 1)];

                            var xaml =
                            $@"
                                <Page xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                                      xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
                                      xmlns:lib='using:{namespaceName}'>
                                    <lib:{shortName}/>
                                </Page>
                            ";

                            element = (UIElement)XamlReader.Load(xaml);
                        }

                        if (element is not null)
                        {
                            Frame.Content = element;
                            return;
                        }
                    }
                }
            }

            if (result is not null && result.Count is not 0)
            {
                MessageDialog dialog = new("Please select a .dll and a .pri file to load.", "Invalid Files Selected");
                await dialog.ShowAsync();
                goto Pick;
            }

            Application.Current.Exit();
        }

        [return: MarshalAs(UnmanagedType.Error)]
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private unsafe delegate int ActivateInstanceElementDelegate(void* _this, out UIElement obj);

        [return: MarshalAs(UnmanagedType.Error)]
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private unsafe delegate int ActivateInstanceIXMPDelegate(void* _this, out IXamlMetadataProvider obj);
    }
}
