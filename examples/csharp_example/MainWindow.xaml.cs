﻿// License: Apache 2.0. See LICENSE file in root directory.
// Copyright(c) 2020-2021 Intel Corporation. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using System.Threading;
using System.Runtime.InteropServices;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Properties;
using System.Windows.Controls.Primitives;
using System.Runtime.Serialization.Formatters.Binary;

namespace rsid_wrapper_csharp
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private rsid.SerialConfig _serialConfig;
        private rsid.PreviewConfig _previewConfig;
        private rsid.Preview _preview;
        private rsid.Authenticator _authenticator;
        private bool _authloopRunning = false;
        private bool _cancelWasCalled = false;
        private rsid.AuthStatus _lastAuthHint = rsid.AuthStatus.Serial_Ok; // To show only changed hints. 
        private WriteableBitmap _previewBitmap;
        private byte[] _previewBuffer = new byte[0]; // store latest frame from the preview callback        
        private object _previewMutex = new object();
        private string[] _userList; // latest user list that was queried from the device
        private rsid.AuthStatus _lastAuthStatus;
        private rsid.EnrollStatus _lastEnrollStatus;
        private bool _waitingForFaceprints = false;
        private IntPtr _mutableFaceprintsHandle = IntPtr.Zero;
        private static int _userIdLength = 16;

        private static readonly Brush ProgressBrush = Application.Current.TryFindResource("ProgressBrush") as Brush;
        private static readonly Brush FailBrush = Application.Current.TryFindResource("FailBrush") as Brush;
        private static readonly Brush SuccessBrush = Application.Current.TryFindResource("SuccessBrush") as Brush;
        private static readonly Brush FgColorBrush = Application.Current.TryFindResource("FgColor") as Brush;
        private static readonly int MaxLogSize = 1024 * 5;
        private IntPtr _signatureHelpeHandle = IntPtr.Zero;
        private enum FlowMode
        {
            Local,
            Server
        }

        private FlowMode _flowMode;

        private struct DatabaseResult
        {
            public rsid.Faceprints faceprints;
            public string userId;
        }

        private class Database
        {
            public Database()
            {
                faceprintsArray = new List<(rsid.Faceprints,string)>();
                lastIndex = 0;
                isDone = (faceprintsArray.Count == 0);

                var baseDir = System.AppDomain.CurrentDomain.BaseDirectory;
                dbPath = Path.Combine(baseDir, "db");
            }

            public bool Push(rsid.Faceprints faceprints, string userId)
            {                
                if (DoesUserExist(userId))                                    
                    return false;                
                else
                {
                    faceprintsArray.Add((faceprints, userId));
                    isDone = false;
                    return true;
                }
            }

            public bool DoesUserExist(string userId)
            {
                return (faceprintsArray.Any(item => item.Item2 == (userId + '\0')));
            }

            public bool Remove(string userId)
            {
                int removedItems = faceprintsArray.RemoveAll(r => r.Item2 == userId);
                return (removedItems > 0);
            }

            public bool RemoveAll()
            {
                faceprintsArray.Clear();
                return (faceprintsArray.Count == 0);
            }

            public (rsid.Faceprints,string,bool) GetNext()
            {
                if (isDone)
                {
                    Console.WriteLine("Scanned all faceprints in db");
                    return (new rsid.Faceprints(), "done", true);
                }
                if (faceprintsArray.Count == 0)
                {
                    Console.WriteLine("Db is empty, can't get next faceprints");
                    return (new rsid.Faceprints(),"empty", true);
                }

                if(lastIndex >= faceprintsArray.Count)                
                    lastIndex = 0;                

                var nextUser       = faceprintsArray[lastIndex];
                var nextFaceprints = nextUser.Item1;
                var nextUserId     = nextUser.Item2;
                
                lastIndex++;
                if (lastIndex >= faceprintsArray.Count)
                {
                    lastIndex = 0;
                    isDone = true;
                }
                
                return (nextFaceprints, nextUserId, false);
            }

            public void ResetIndex()
            {
                lastIndex = 0;
                isDone = isDone = (faceprintsArray.Count == 0);
            }

            public void GetUserIds(out string[] userIds)
            {
                int arrayLength = faceprintsArray.Count;
                userIds = new string[arrayLength];
                for (var i = 0; i < arrayLength; i++)                
                    userIds[i] = faceprintsArray[i].Item2;                
            }

            public void Save()
            {
                try
                {
                    FileStream stream = new FileStream(dbPath, FileMode.Create);
                    BinaryFormatter formatter = new BinaryFormatter();
                    formatter.Serialize(stream, faceprintsArray);
                    stream.Close();                    
                }
                catch (System.Exception e)
                {
                    System.Console.WriteLine($"Failed saving database: {e.Message}");
                }
            }

            public void Load()
            {
                try
                {
                    Console.WriteLine("Loading database ...");
                    if (!File.Exists(dbPath))
                    {
                        Console.WriteLine("Database file is missing, using an empty database.");
                        return;
                    }                    
                    FileStream inStr = new FileStream(dbPath, FileMode.Open);
                    BinaryFormatter bf = new BinaryFormatter();
                    faceprintsArray = bf.Deserialize(inStr) as List<(rsid.Faceprints, string)>;
                }
                catch (System.Exception e)
                {
                    System.Console.WriteLine($"Failed loading database: {e.Message}");
                }
            }

            public List<(rsid.Faceprints,string)> faceprintsArray;
            public int lastIndex;
            public bool isDone;
            public string dbPath;
        }

        private Database _db = new Database();

        public MainWindow()
        {
            InitializeComponent();

            // start with hidden console
            AllocConsole();
            ToggleConsoleAsync(false);

            ContentRendered += MainWindow_ContentRendered;
            Closing += MainWindow_Closing;
            Microsoft.Win32.SystemEvents.SessionSwitch += SystemEvents_SessionSwitch;
        }

        private void LoadConfig()
        {
            var serialPort = string.Empty;
            var serType = rsid.SerialType.USB;

            var autoDetect = Settings.Default.AutoDetect;
            if (autoDetect)
            {
                var enumerator = new DeviceEnumerator();
                var enumeration = enumerator.Enumerate();

                if (enumeration.Count == 1)
                {
                    serialPort = enumeration[0].port;
                    serType = enumeration[0].serialType;
                }
                else
                {
                    var msg = "Serial port auto detection failed!\n\n";
                    msg += $"Expected 1 connected port, but found {enumeration.Count}.\n";
                    msg += "Please make sure the device is connected properly.\n\n";
                    msg += "To manually specify the serial port:\n";
                    msg += "  1. Set \"AutoDetect\" to False.\n";
                    msg += "  2. Set \"Port\" to your serial port.\n";
                    msg += "  3. Set \"SerialType\" to either USB or UART.\n";

                    MessageBox.Show(msg, "Configuration Error", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                    Application.Current.Shutdown();
                }
            }
            else
            {
                serialPort = Settings.Default.Port;
                var serialTypeString = Settings.Default.SerialType.ToUpper();
                serType = serialTypeString == "UART" ? rsid.SerialType.UART : rsid.SerialType.USB;
            }

            _serialConfig = new rsid.SerialConfig { port = serialPort, serialType = serType };

            var cameraNumber = Settings.Default.CameraNumber;
            _previewConfig = new rsid.PreviewConfig { cameraNumber = cameraNumber };

            _flowMode = StringToFlowMode(Settings.Default.FlowMode.ToUpper());
            if (_flowMode == FlowMode.Server)
            {                
                StandbyBtn.IsEnabled = false;
                _db.Load();
            }
        }

        private FlowMode StringToFlowMode(string flowModeString)
        {
            if (flowModeString == "SERVER")
            {
                ShowLog("Server Mode");
                return FlowMode.Server;
            }
            else if (flowModeString == "LOCAL")
            {
                ShowLog("Local Mode");
                return FlowMode.Local;
            }

            ShowFailedStatus("Mode " + flowModeString + " not supported, using Local instead");
            ShowLog("Local Mode");
            return FlowMode.Local;
        }

        // start/stop preview on lock/unlock windows session
        private void SystemEvents_SessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
        {
            if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock)
                _preview.Start(OnPreview);
            else if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock)
                _preview.Stop();
        }

        // Dispatch with background priority
        private void BackgroundDispatch(Action action)
        {
            Dispatcher.BeginInvoke(action, DispatcherPriority.Background, null);
        }

        // Dispatch with Render priority
        private void RenderDispatch(Action action)
        {
            try
            {
                Dispatcher.BeginInvoke(action, DispatcherPriority.Render, null);
            }
            catch (Exception ex)
            {
                Console.WriteLine("RenderDispatch: " + ex.Message);
            }
        }

        // Create authenticator 
        private rsid.Authenticator CreateAuthenticator()
        {
            _signatureHelpeHandle = rsid_create_example_sig_clbk();
            var sigCallback = (rsid.SignatureCallback)Marshal.PtrToStructure(_signatureHelpeHandle, typeof(rsid.SignatureCallback));
            return new rsid.Authenticator(sigCallback);
        }


        // show lib and fw version both on left panel and summary in top window title
        private void PingAndShowVersions()
        {
            // show lib version
            var hostVersion = _authenticator.Version();
            ShowLog("Host: v" + hostVersion);            
            var title = $"RealSenseID v{hostVersion}";

            // show fw module versions
            using (var controller = new rsid.DeviceController())
            {
                ShowLog("Connecting..");
                var status = controller.Connect(_serialConfig);
                if(status != rsid.Status.Ok)
                {
                    ShowLog("Failed");
                    throw new Exception("Connection failed");
                }
                ShowLog("Connection Ok");
                ShowLog("");

                if (controller.Ping() != rsid.Status.Ok)
                {
                    ShowLog("Ping failed");
                    throw new Exception("Connection failed");
                }
                //show fw versions and device serial number
                var fwVersion = controller.QueryFirmwareVersion();
                var versionLines = fwVersion.ToLower().Split('|');
                //var sn = controller.QuerySerialNumber();

                //ShowLog("S/N: " + sn);

                ShowLog("Firmware:");
                foreach (var v in versionLines)
                {
                    ShowLog(" * " + v);
                    if (v.Contains("opfw"))
                    {
                        var splitted = v.Split(':');
                        if (splitted.Length == 2)
                            title += $" (firmware {splitted[1]})";                        
                    }
                }
            }
            ShowLog("");
            Dispatcher.BeginInvoke(new Action(() => { Title = title; }));
        }

        private rsid.AuthConfig QueryAuthSettings()
        {
            ShowLog("");
            ShowLog("Query settings..");
            rsid.AuthConfig authConfig;
            var rv = _authenticator.QueryAuthSettings(out authConfig);
            if (rv != rsid.Status.Ok)
            {
                throw new Exception("Query error: " + rv.ToString());
            }

            ShowLog(" * " + authConfig.cameraRotation.ToString());
            ShowLog(" * Security " + authConfig.securityLevel.ToString());
            ShowLog("");
            return authConfig;
        }
        // query user list from the device and update the display
        private void RefreshUserList()
        {
            // Query users and update the user list display            
            ShowLog("Query users..");
            string[] users;
            var rv = _authenticator.QueryUserIds(out users);
            if (rv != rsid.Status.Ok)
            {
                throw new Exception("Query error: " + rv.ToString());
            }

            ShowLog($"{users.Length} users");

            // update the gui and save the list into _userList
            BackgroundDispatch(() =>
            {
                UsersListTxtBox.Text = string.Empty;
                if (users.Any()) UsersListTxtBox.Text = $"{users.Count()} Users\n=========\n";
                foreach (var userId in users)
                {
                    UsersListTxtBox.Text += $"{userId}\n";
                }
            });
            _userList = users;
        }

        // show/hide console
        private void ToggleConsoleAsync(bool show)
        {
            const int SW_HIDE = 0;
            const int SW_SHOW = 5;
            ShowWindow(GetConsoleWindow(), show ? SW_SHOW : SW_HIDE);
        }

        private void MainWindow_ContentRendered(object sender, EventArgs e)
        {
            // load serial port and preview configuration
            LoadConfig();

            // create face authenticator and show version
            _authenticator = CreateAuthenticator();
                        
            // pair to the device       
            OnStartSession(string.Empty);
            ThreadPool.QueueUserWorkItem(InitialSession);
        }

        private void MainWindow_Closing(object sender, EventArgs e)
        {
            if (_authloopRunning)
            {
                try
                {
                    _cancelWasCalled = true;
                    _authenticator.Cancel();
                }
                catch { }
            }
        }

        private void OnStartSession(string title)
        {
            Dispatcher.Invoke(() =>
            {
                if (!string.IsNullOrEmpty(title)) LogTextBox.Text = title + "\n===========\n";
                LogScroll.ScrollToEnd();
                EnrollBtn.IsEnabled = false;
                AuthBtn.IsEnabled = false;
                DeleteUsersBtn.IsEnabled = false;
                AuthLoopBtn.IsEnabled = false;
                AuthSettingsBtn.IsEnabled = false;
                StandbyBtn.IsEnabled = false;
                _cancelWasCalled = false;
                RedDot.Visibility = Visibility.Visible;
                _lastAuthHint = rsid.AuthStatus.Serial_Ok;
            });
        }

        private void OnStopSession()
        {
            Dispatcher.Invoke(() =>
            {
                EnrollBtn.IsEnabled = true;
                AuthBtn.IsEnabled = true;
                DeleteUsersBtn.IsEnabled = true;
                AuthLoopBtn.IsEnabled = true;
                AuthSettingsBtn.IsEnabled = true;
                if (_flowMode != FlowMode.Server)				                	
                	StandbyBtn.IsEnabled = true;				
                RedDot.Visibility = Visibility.Hidden;
            });
        }


        private void ShowLog(string message)
        {
            BackgroundDispatch(() =>
            {
                // keep log panel size under control
                if (LogTextBox.Text.Length > MaxLogSize)
                {
                    LogTextBox.Text = "";
                }
                // add log line
                LogTextBox.Text += message + "\n";
            });
        }


        private void ShowTitle(string message, Brush color)
        {

            BackgroundDispatch(() =>
            {
                StatusLabel.Content = message;
                StatusLabel.Background = color;
            });
        }

        private void ShowSuccessTitle(string message)
        {
            ShowTitle(message, SuccessBrush);
        }

        private void ShowFailedStatus(string message)
        {
            ShowTitle(message, FailBrush);
        }

        private void ShowFailedStatus(rsid.AuthStatus status)
        {
            // show "Authenticate Failed" message on serial errors
            var msg = (int)status > (int)rsid.AuthStatus.Serial_Ok ? "Authenticate Failed" : status.ToString();
            ShowFailedStatus(msg);
        }

        private void ShowFailedStatus(rsid.EnrollStatus status)
        {
            // show "Enroll Failed" message on serial errors
            var msg = (int)status > (int)rsid.EnrollStatus.Serial_Ok ? "Enroll Failed" : status.ToString();
            ShowFailedStatus(msg);
        }

        private void ShowProgressTitle(string message)
        {
            ShowTitle(message, ProgressBrush);
        }

        private bool ConnectAuth()
        {
            var status = _authenticator.Connect(_serialConfig);
            if (status != rsid.Status.Ok)
            {
                ShowFailedStatus("Connection Error");
                ShowLog("Connection error");
                MessageBox.Show($"Connection Error.\n\nPlease check the serial port setting in the config file.",
                    $"Connection Failed to Port {_serialConfig.port}", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            return true;
        }


        // Enroll callbacks
        private void OnEnrollHint(rsid.EnrollStatus hint, IntPtr ctx)
        {
            ShowLog(hint.ToString());
        }

        private void OnEnrollProgress(rsid.FacePose pose, IntPtr ctx)
        {
            ShowLog(pose.ToString());
        }

        private void OnEnrollResult(rsid.EnrollStatus status, IntPtr ctx)
        {
            ShowLog(status.ToString());
            _lastEnrollStatus = rsid.EnrollStatus.Failure;

            if (_cancelWasCalled)
            {
                ShowSuccessTitle("Enroll Canceled");
                ShowFailedStatus("Canceled");
            }
            else if (status == rsid.EnrollStatus.Success)
            {
                _lastEnrollStatus = rsid.EnrollStatus.Success;
                ShowSuccessTitle("Enroll Success");
            }
            else
            {
                ShowFailedStatus(status);
            }
        }

        // Authentication callbacks
        private void OnAuthHint(rsid.AuthStatus hint, IntPtr ctx)
        {
            if (_lastAuthHint != hint)
            {
                _lastAuthHint = hint;
                ShowLog(hint.ToString());
            }
        }

        private void OnAuthResult(rsid.AuthStatus status, string userId, IntPtr ctx)
        {
            _lastAuthStatus = rsid.AuthStatus.Failure;

            if (_cancelWasCalled)
            {
                ShowSuccessTitle("Authentication Canceled");
                ShowFailedStatus("Canceled");
            }
            else if (status == rsid.AuthStatus.Success)
            {
                _lastAuthStatus = rsid.AuthStatus.Success;
                ShowLog($"Success \"{userId}\"");
                ShowSuccessTitle($"{userId}");
            }
            else
            {
                ShowLog(status.ToString());
                ShowFailedStatus(status);
            }
            _lastAuthHint = rsid.AuthStatus.Serial_Ok; // show next hint, session is done
        }

        private void OnAuthExtractionResult(rsid.AuthStatus status, IntPtr ctx)
        {
            _lastAuthStatus = rsid.AuthStatus.Failure;

            if (_cancelWasCalled)
            {
                ShowSuccessTitle("Authentication Canceled");
                ShowFailedStatus("Canceled");
            }
            else if (status == rsid.AuthStatus.Success)
            {
                _lastAuthStatus = rsid.AuthStatus.Success;
            }
            else
            {
                ShowLog(status.ToString());
                ShowFailedStatus(status);
            }
            _lastAuthHint = rsid.AuthStatus.Serial_Ok; // show next hint, session is done
        }

        private void UIHandlePreview(int width, int height, int stride)
        {
            var targetWidth = (int)PreviewImage.Width;
            var targetHeight = (int)PreviewImage.Height;

            //creae writable bitmap if not exists or if image size changed
            if (_previewBitmap == null || targetWidth != width || targetHeight != height)
            {
                PreviewImage.Width = width;
                PreviewImage.Height = height;
                Console.WriteLine($"Creating new WriteableBitmap preview buffer {width}x{height}");
                _previewBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr24, null);
                PreviewImage.Source = _previewBitmap;
                // Hide preview placeholder once we have first frame
                LabelPreview.Visibility = Visibility.Collapsed;
            }
            Int32Rect sourceRect = new Int32Rect(0, 0, width, height);
            lock (_previewMutex)
            {
                _previewBitmap.WritePixels(sourceRect, _previewBuffer, stride, 0);
            }
        }

        // Handle preview callback.         
        private void OnPreview(rsid.PreviewImage image, IntPtr ctx)
        {
            if (image.height < 2) return;

            lock (_previewMutex)
            {
                if (_previewBuffer.Length != image.size)
                {
                    Console.WriteLine("Creating preview buffer");
                    _previewBuffer = new byte[image.size];
                }
                Marshal.Copy(image.buffer, _previewBuffer, 0, image.size);
            }
            RenderDispatch(() => UIHandlePreview(image.width, image.height, image.stride));
        }
        
        // 1. Connect and pair to the device
        // 2. Start preview
        // 3. Query some initial info from the device:
        //   * FW Version        
        //   * Auth settings
        //   * List of enrolled users        
        private void InitialSession(Object threadContext)
        {            
            IntPtr pairArgsHandle = IntPtr.Zero;
            try
            {
                PingAndShowVersions();
                if (!ConnectAuth())
                {
                    throw new Exception("Connection failed");
                }

                // start preview                
                _preview = new rsid.Preview(_previewConfig);
                _preview.Start(OnPreview);

                ShowLog("Pairing..");                
                pairArgsHandle = rsid_create_pairing_args_example(_signatureHelpeHandle);
                var pairingArgs = (rsid.PairingArgs)Marshal.PtrToStructure(pairArgsHandle, typeof(rsid.PairingArgs));

                var rv = _authenticator.Pair(ref pairingArgs);
                if (rv != rsid.Status.Ok)
                {
                    throw new Exception("Failed pairing");
                }
                ShowLog("Pairing Ok");
                rsid_update_device_pubkey_example(_signatureHelpeHandle, Marshal.UnsafeAddrOfPinnedArrayElement(pairingArgs.DevicePubkey, 0));

                QueryAuthSettings();
				if(_flowMode == FlowMode.Server)
	                RefreshUserListServer();
				else
					RefreshUserList();

            }
            catch (Exception ex)
            {
                ShowFailedStatus(ex.Message);
            }
            finally
            {
                if (pairArgsHandle != IntPtr.Zero) rsid_destroy_pairing_args_example(pairArgsHandle);
                OnStopSession();
                _authenticator.Disconnect();
            }
        }

        // Enroll Job
        private void EnrollJob(Object threadContext)
        {
            var userId = threadContext as string;

            if (!ConnectAuth()) return;
            OnStartSession($"Enroll \"{userId}\"");
            IntPtr userIdCtx = Marshal.StringToHGlobalUni(userId);
            try
            {
                ShowProgressTitle("Enrolling..");
                var enrollArgs = new rsid.EnrollArgs
                {
                    userId = userId,
                    hintClbk = OnEnrollHint,
                    resultClbk = OnEnrollResult,
                    progressClbk = OnEnrollProgress,
                    ctx = userIdCtx
                };
                var status = _authenticator.Enroll(enrollArgs);
                if (status == rsid.Status.Ok) RefreshUserList();
            }
            catch (Exception ex)
            {
                ShowFailedStatus(ex.Message);
            }
            finally
            {
                OnStopSession();
                _authenticator.Disconnect();
                Marshal.FreeHGlobal(userIdCtx);
            }
        }

        // Authenticate job
        private void AuthenticateJob(Object threadContext)
        {
            if (!ConnectAuth()) return;
            OnStartSession("Authenticate");
            try
            {
                var authArgs = new rsid.AuthArgs { hintClbk = OnAuthHint, resultClbk = OnAuthResult, ctx = IntPtr.Zero };
                ShowProgressTitle("Authenticating..");
                _authenticator.Authenticate(authArgs);
            }
            catch (Exception ex)
            {
                ShowFailedStatus(ex.Message);
            }
            finally
            {
                OnStopSession();
                _authenticator.Disconnect();
            }
        }

        // SetAuthSettings job
        private void SetAuthSettingsJob(Object threadContext)
        {
            if (!ConnectAuth()) return;

            var authConfig = (rsid.AuthConfig)threadContext;
            OnStartSession("SetAuthSettings");
            try
            {
                ShowProgressTitle("SetAuthSettings " + authConfig.securityLevel.ToString());
                ShowLog("Security " + authConfig.securityLevel.ToString());
                ShowLog(authConfig.cameraRotation.ToString().Replace("_", " "));
                var status = _authenticator.SetAuthSettings(authConfig);

                if (status == rsid.Status.Ok)
                {
                    ShowSuccessTitle("AuthSettings Done");
                    ShowLog("Ok");
                }
                else
                {
                    ShowFailedStatus("SetAuthSettings: Failed");
                    ShowLog("Failed");
                }
            }
            catch (Exception ex)
            {
                ShowFailedStatus(ex.Message);
            }
            finally
            {
                OnStopSession();
                _authenticator.Disconnect();
            }
        }

        private void StandbyJob(Object threadContext)
        {
            if (!ConnectAuth()) return;

            OnStartSession("Standby");
            try
            {
                ShowProgressTitle("Standby");                
                var status = _authenticator.Standby();

                if (status == rsid.Status.Ok)
                {
                    ShowSuccessTitle("Standby Done");
                    ShowLog("Ok");
                }
                else
                {
                    ShowFailedStatus("Standby: Failed");
                    ShowLog("Failed");
                }
            }
            catch (Exception ex)
            {
                ShowFailedStatus(ex.Message);
            }
            finally
            {
                OnStopSession();
                _authenticator.Disconnect();
            }
        }


        // Toggle auth loop button content (start/stop images and tooltips)
        private void ToggleLoopButton(bool isRunning)
        {
            Dispatcher.Invoke(() =>
            {
                if (isRunning)
                {
                    AuthLoopBtn.Foreground = Brushes.Red;
                    AuthLoopBtn.ToolTip = "Cancel";
                    AuthLoopBtn.Content = "\u275A\u275A"; //  "pause" symbol
                    AuthLoopBtn.FontSize = 13;

                }
                else
                {
                    AuthLoopBtn.Foreground = FgColorBrush;
                    AuthLoopBtn.ToolTip = "Authentication Loop";
                    AuthLoopBtn.Content = "\u221e"; // the "infinite" symbol
                    AuthLoopBtn.FontSize = 14;
                }
                AuthLoopBtn.IsEnabled = true;
            });
        }


        // Authentication loop job
        private void AuthenticateLoopJob(Object threadContext)
        {
            if (!ConnectAuth()) return;

            OnStartSession("Auth Loop");
            try
            {
                var authArgs = new rsid.AuthArgs { hintClbk = OnAuthHint, resultClbk = OnAuthResult, ctx = IntPtr.Zero };
                ShowProgressTitle("Authenticating..");
                _authloopRunning = true;
                ToggleLoopButton(true);
                _authenticator.AuthenticateLoop(authArgs);
            }
            catch (Exception ex)
            {
                try
                {
                    _authenticator.Cancel(); //try to cancel the auth loop
                }
                catch { }
                ShowFailedStatus(ex.Message);
            }
            finally
            {
                ToggleLoopButton(false);
                OnStopSession();
                _authloopRunning = false;
                _authenticator.Disconnect();
            }

        }

        // Cancel job
        private void CancelJob(Object threadContext)
        {
            try
            {
                ShowProgressTitle("Cancel..");
                ShowLog("Cancel..");
                _cancelWasCalled = true;
                var status = _authenticator.Cancel();
                ShowLog($"Cancel status: {status}");
                if (status == rsid.Status.Ok)
                {
                    ShowSuccessTitle("Cancel Ok");
                }
                else
                {
                    ShowFailedStatus("Cancel Failed");
                }
            }
            catch (Exception ex)
            {
                ShowFailedStatus(ex.Message);
            }
        }

        private void DeleteUsersJob(Object threadContext)
        {
            if (!ConnectAuth()) return;
            OnStartSession("Delete Users");
            try
            {
                ShowProgressTitle("Deleting..");
                var status = _authenticator.RemoveAllUsers();

                if (status == rsid.Status.Ok)
                {
                    ShowSuccessTitle("Delete: Ok");
                    ShowLog("Ok");
                    RefreshUserList();
                }
                else
                {
                    ShowFailedStatus("Delete: Failed");
                    ShowLog("Failed");
                }
            }
            catch (Exception ex)
            {
                ShowFailedStatus(ex.Message);
            }
            finally
            {
                OnStopSession();
                _authenticator.Disconnect();
            }
        }

        private bool GetNextFaceprints(IntPtr faceprints, out string userId)
        {            
            var db_result = _db.GetNext();
            var newFaceprints = db_result.Item1;
            userId = String.Copy(db_result.Item2);
            var isDone = db_result.Item3;

            if (isDone)
                return false;

            Marshal.StructureToPtr(newFaceprints, faceprints, false);
            return true;
        }
		
        // Authenticate faceprints extraction job
        private void AuthenticateExtractFaceprintsJob(Object threadContext)
        {
            if (!ConnectAuth()) return;
            OnStartSession("Extracting Faceprints");
            try
            {
                IntPtr authFaceprintsHandle = _authenticator.CreateFaceprints();
                var authExtArgs = new rsid.AuthExtractArgs { hintClbk = OnAuthHint, resultClbk = OnAuthExtractionResult, ctx = IntPtr.Zero, faceprints = authFaceprintsHandle };
                ShowProgressTitle("Extracting faceprints for authentication ..");
                _authenticator.AuthenticateExtractFaceprints(authExtArgs);
                if (_lastAuthStatus == rsid.AuthStatus.Success)
                {
                    var authFaceprints = (rsid.Faceprints)Marshal.PtrToStructure(authFaceprintsHandle, typeof(rsid.Faceprints));
                    Match(authFaceprints);
                }
                _authenticator.DestroyFaceprints(authFaceprintsHandle);
            }
            catch (Exception ex)
            {
                ShowFailedStatus(ex.Message);
            }
            finally
            {
                OnStopSession();
                _authenticator.Disconnect();
            }
        }

        public void OnAuthLoopResult(rsid.AuthStatus status, IntPtr ctx)
        {
            _lastAuthStatus = rsid.AuthStatus.Failure;

            if (_cancelWasCalled)
            {
                ShowSuccessTitle("Authentication Canceled");
                ShowFailedStatus("Canceled");
            }
            else if(_waitingForFaceprints)
            {
                var faceprints = (rsid.Faceprints)Marshal.PtrToStructure(_mutableFaceprintsHandle, typeof(rsid.Faceprints));
                Match(faceprints);
                _waitingForFaceprints = false;
            }
            else if (status == rsid.AuthStatus.Success)
            {
                _lastAuthStatus = rsid.AuthStatus.Success;
                _waitingForFaceprints = true;
            }
            else
            {
                ShowLog(status.ToString());
                ShowFailedStatus(status);
            }
            _lastAuthHint = rsid.AuthStatus.Serial_Ok; // show next hint, session is done
        }

        // Authenticate loop faceprints extraction job
        private void AuthenticateExtractFaceprintsLoopJob(Object threadContext)
        {
            if (!ConnectAuth()) return;

            OnStartSession("Authentication faceprints extraction loop");
            try
            {
                _waitingForFaceprints = false;
                _mutableFaceprintsHandle = _authenticator.CreateFaceprints();                
                var authLoopExtArgs = new rsid.AuthExtractArgs { hintClbk = OnAuthHint, resultClbk = OnAuthLoopResult, ctx = IntPtr.Zero, faceprints = _mutableFaceprintsHandle };
                ShowProgressTitle("Authenticating..");
                _authloopRunning = true;
                ToggleLoopButton(true);                
                _authenticator.AuthenticateLoopExtractFaceprints(authLoopExtArgs);
                _authenticator.DestroyFaceprints(_mutableFaceprintsHandle);
            }
            catch (Exception ex)
            {
                try
                {
                    _authenticator.Cancel(); //try to cancel the auth loop
                }
                catch { }
                ShowFailedStatus(ex.Message);
            }
            finally
            {
                ToggleLoopButton(false);
                OnStopSession();
                _authloopRunning = false;
                _authenticator.Disconnect();
            }
        }

        // Enroll Job
        private void EnrollExtractFaceprintsJob(Object threadContext)
        {
            var userId = threadContext as string;
            if (_db.DoesUserExist(userId))
            {
                ShowFailedStatus("User ID already exists in database");
                return;
            }

            if (!ConnectAuth()) return;
            OnStartSession($"Enroll \"{userId}\"");
            try
            {
                ShowProgressTitle("Extracting Faceprints");
                IntPtr enrollFaceprintsHandle = _authenticator.CreateFaceprints();
                userId = userId + '\0';
                _lastEnrollStatus = rsid.EnrollStatus.Failure;
                var enrollExtArgs = new rsid.EnrollExtractArgs
                {
                    userId = userId,
                    hintClbk = OnEnrollHint,
                    resultClbk = OnEnrollResult,
                    progressClbk = OnEnrollProgress,
                    faceprints = enrollFaceprintsHandle
                };
                var operationStatus = _authenticator.EnrollExtractFaceprints(enrollExtArgs);
                if (_lastEnrollStatus == rsid.EnrollStatus.Success)
                {
                    var enrolledFaceprints = (rsid.Faceprints)Marshal.PtrToStructure(enrollFaceprintsHandle, typeof(rsid.Faceprints));
                    var push_success = _db.Push(enrolledFaceprints, userId);
                    if (push_success)
                        _db.Save();
                    RefreshUserListServer();                    
                }
                _authenticator.DestroyFaceprints(enrollFaceprintsHandle);
            }
            catch (Exception ex)
            {
                ShowFailedStatus(ex.Message);
            }
            finally
            {
                OnStopSession();
                _authenticator.Disconnect();

            }
        }

        private void ShowMatchResult(rsid.MatchResult matchResult, string userId)
        {
            if (matchResult.success==1)
            {
                ShowSuccessTitle("Match with " + userId + " !");
                ShowLog("Match with " + userId + " !");
            }
            else
            {
                ShowFailedStatus("Faceprints extracted but did not match any user");
                ShowLog("Faceprints extracted but did not match any user");
            }
        }

        private void Match(rsid.Faceprints faceprintsToMatch)
        {            
            try
            {                
                ShowProgressTitle("Matching faceprints to database");

                _db.ResetIndex();

                IntPtr faceprintsHandle = _authenticator.CreateFaceprints();
                Marshal.StructureToPtr(faceprintsToMatch, faceprintsHandle, false);

                //rsid.Faceprints faceprintsDb = new rsid.Faceprints();
                IntPtr faceprintsDbHandle = _authenticator.CreateFaceprints();                
                string userIdDb = new string('\0', _userIdLength);

                IntPtr updatedFaceprintsHandle = _authenticator.CreateFaceprints();
                rsid.MatchResult matchResult = new rsid.MatchResult { success = 0, shouldUpdate = 0 };

                while (GetNextFaceprints(faceprintsDbHandle, out userIdDb))
                {
                    //Marshal.PtrToStructure(faceprintsDbHandle, faceprintsDb);

                    var matchArgs = new rsid.MatchArgs { faceprints1 = faceprintsHandle, faceprints2 = faceprintsDbHandle, updatedFaceprints = updatedFaceprintsHandle };
                    var matchResultHandle = _authenticator.MatchFaceprintsToFaceprints(matchArgs);                    

                    matchResult = (rsid.MatchResult) Marshal.PtrToStructure(matchResultHandle, typeof(rsid.MatchResult));                    
                    var updatedFaceprints = Marshal.PtrToStructure(updatedFaceprintsHandle, typeof(rsid.Faceprints));                    

                    if (matchResult.success == 1)
                        break;
                }

                ShowMatchResult(matchResult, userIdDb);

                _authenticator.DestroyFaceprints(faceprintsHandle);
                _authenticator.DestroyFaceprints(faceprintsDbHandle);
                _authenticator.DestroyFaceprints(updatedFaceprintsHandle);
            }
            catch (Exception ex)
            {
                ShowFailedStatus(ex.Message);
            }
        }
        
        private void DeleteUsersServerJob(Object threadContext)
        {
            if (!ConnectAuth()) return;
            OnStartSession("Delete Users");
            try
            {
                ShowProgressTitle("Deleting..");
                var success = _db.RemoveAll();
                _db.Save();

                if (success)
                {
                    ShowSuccessTitle("Delete All: Ok");
                    ShowLog("Ok");
                    RefreshUserListServer();
                }
                else
                {
                    ShowFailedStatus("Delete All: Failed");
                    ShowLog("Failed");
                }
            }
            catch (Exception ex)
            {
                ShowFailedStatus(ex.Message);
            }
            finally
            {
                OnStopSession();
                _authenticator.Disconnect();
            }
        }
        
        private void RefreshUserListServer()
        {
            // Query users and update the user list display            
            ShowLog("Query users..");
            string[] users;
            _db.GetUserIds(out users);
            ShowLog($"{users.Length} users");

            // update the gui and save the list into _userList
            BackgroundDispatch(() =>
            {
                UsersListTxtBox.Text = string.Empty;
                if (users.Any()) UsersListTxtBox.Text = $"{users.Count()} Users\n=========\n";
                foreach (var userId in users)
                {
                    UsersListTxtBox.Text += $"{userId}\n";
                }
            });
            _userList = users;
        }

        private void DeleteSingleUserServerJob(Object threadContext)
        {            
            var userId = (string)threadContext;            

            try
            {
                ShowProgressTitle("Deleting..");
                var success = _db.Remove(userId);
                _db.Save();

                if (success)
                {
                    ShowSuccessTitle("Delete: Ok");
                    ShowLog("Ok");
                    RefreshUserListServer();
                }
                else
                {
                    ShowFailedStatus("Delete: Failed");
                    ShowLog("Failed");
                }
            }
            catch (Exception ex)
            {
                ShowFailedStatus(ex.Message);
            }
            finally
            {
                OnStopSession();
                _authenticator.Disconnect();
            }
        }

        // Wrapper method for use with thread pool.
        private void DeleteSingleUserJob(Object threadContext)
        {
            if (!ConnectAuth()) return;
            var userId = (string)threadContext;
            OnStartSession($"Delete \"{userId}\"");

            try
            {
                ShowProgressTitle("Deleting..");
                var status = _authenticator.RemoveUser(userId);

                if (status == rsid.Status.Ok)
                {
                    ShowSuccessTitle("Delete: Ok");
                    ShowLog("Ok");
					if(_flowMode == FlowMode.Server)
		                RefreshUserListServer();
					else
						RefreshUserList();					
                }
                else
                {
                    ShowFailedStatus("Delete: Failed");
                    ShowLog("Failed");
                }
            }
            catch (Exception ex)
            {
                ShowFailedStatus(ex.Message);
            }
            finally
            {
                OnStopSession();
                _authenticator.Disconnect();
            }
        }
        //
        //
        // Button Handlers
        //
        private void Auth_Click(object sender, RoutedEventArgs e)
        {
			if(_flowMode == FlowMode.Server)            
				ThreadPool.QueueUserWorkItem(AuthenticateExtractFaceprintsJob);
			else
				ThreadPool.QueueUserWorkItem(AuthenticateJob);
        }

        private void AuthLoop_Click(object sender, RoutedEventArgs e)
        {
            if (_authloopRunning)
            {
                // cancel auth loop.
                // disable cancel button until canceled.
                AuthLoopBtn.IsEnabled = false;
                ThreadPool.QueueUserWorkItem(CancelJob);
            }
            else
            {
                // start auth loop.
                // enable cancel button until canceled.s
                AuthLoopBtn.IsEnabled = true;
                if (_flowMode==FlowMode.Server)
                    ThreadPool.QueueUserWorkItem(AuthenticateExtractFaceprintsLoopJob);
                else
                    ThreadPool.QueueUserWorkItem(AuthenticateLoopJob);                                
            }

        }

        private void Enroll_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new EnrollInput();
            if (dialog.ShowDialog() == true)
            {    
				if(_flowMode == FlowMode.Server)            
                	ThreadPool.QueueUserWorkItem(EnrollExtractFaceprintsJob, dialog.EnrolledUsername);
				else
					ThreadPool.QueueUserWorkItem(EnrollJob, dialog.EnrolledUsername);
            }
        }

        private void DeleteUsers_Click(object sender, RoutedEventArgs e)
        {

            var dialog = new DeleteUserInput(_userList);
            if (dialog.ShowDialog() == true)
            {
				if(_flowMode == FlowMode.Server)  
				{
	                if (dialog.DeleteAll)
	                    ThreadPool.QueueUserWorkItem(DeleteUsersServerJob);
	                else if (!string.IsNullOrWhiteSpace(dialog.SelectedUser))
	                    ThreadPool.QueueUserWorkItem(DeleteSingleUserServerJob, dialog.SelectedUser);
				}
				else
				{
					if (dialog.DeleteAll)
	                    ThreadPool.QueueUserWorkItem(DeleteUsersJob);
	                else if (!string.IsNullOrWhiteSpace(dialog.SelectedUser))
	                    ThreadPool.QueueUserWorkItem(DeleteSingleUserJob, dialog.SelectedUser);
				}				
            }
        }

        private void AuthSettings_Click(object sender, RoutedEventArgs e)
        {
            if (!ConnectAuth()) return;
            var authConfig = QueryAuthSettings();
            var dialog = new AuthSettingsInput(authConfig);
            if (dialog.ShowDialog() == true)
            {
                ThreadPool.QueueUserWorkItem(SetAuthSettingsJob, dialog.Config);
            }
        }

        private void StandbyBtn_Click(object sender, RoutedEventArgs e)
        {
            ThreadPool.QueueUserWorkItem(StandbyJob);
        }

        // show/hide console
        private void ShowLogChkbox_Click(object sender, RoutedEventArgs e)
        {
            ToggleConsoleAsync(ShowLogCheckbox.IsChecked.GetValueOrDefault());
        }

        private void PreviewImage_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            LabelPlayStop.Visibility = LabelPlayStop.Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible;
            if (LabelPlayStop.Visibility == Visibility.Hidden)
            {
                _preview.Start(OnPreview);
                PreviewImage.Opacity = 1.0;
            }
            else
            {
                _preview.Stop();
                PreviewImage.Opacity = 0.7;
            }
        }

        // Debug console support
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AllocConsole();

        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

#if DEBUG
        private const string dllName = "rsid_signature_example_debug";
#else
        private const string dllName = "rsid_signature_example";
#endif        
        [DllImport(dllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        static extern IntPtr rsid_create_example_sig_clbk();

        [DllImport(dllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        static extern void rsid_destroy_example_sig_clbk(IntPtr rsid_signature_clbk);

        [DllImport(dllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        static extern IntPtr rsid_get_host_pubkey_example(IntPtr rsid_signature_clbk);

        [DllImport(dllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        static extern IntPtr rsid_update_device_pubkey_example(IntPtr rsid_signature_clbk, IntPtr device_key);

        [DllImport(dllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        static extern IntPtr rsid_create_pairing_args_example(IntPtr rsid_signature_clbk);

        [DllImport(dllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        static extern void rsid_destroy_pairing_args_example(IntPtr pairing_args);
    }
}