﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using Mesen.GUI.Config;
using Mesen.GUI.Debugger;
using Mesen.GUI.Forms.Cheats;
using Mesen.GUI.Forms.Config;
using Mesen.GUI.Forms.NetPlay;
using Mesen.GUI.GoogleDriveIntegration;

namespace Mesen.GUI.Forms
{
	public partial class frmMain : BaseForm, IMessageFilter
	{
		private InteropEmu.NotificationListener _notifListener;
		private Thread _emuThread;
		private frmDebugger _debugger;
		private frmLogWindow _logWindow;
		private frmCheatList _cheatListWindow;
		private string _currentRomPath = null;
		private int _currentRomArchiveIndex = -1;
		private string _currentGame = null;
		private bool _customSize = false;
		private FormWindowState _originalWindowState;
		private bool _fullscreenMode = false;
		private double _regularScale = ConfigManager.Config.VideoInfo.VideoScale;
		private bool _isNsfPlayerMode = false;
		private object _loadRomLock = new object();
		private int _romLoadCounter = 0;
		private bool _removeFocus = false;

		private string[] _commandLineArgs;
		private bool _noAudio = false;
		private bool _noVideo = false;
		private bool _noInput = false;

		private PrivateFontCollection _fonts = new PrivateFontCollection();

		public frmMain(string[] args)
		{
			InitializeComponent();

			if(ConfigManager.Config.WindowLocation.HasValue) {
				this.StartPosition = FormStartPosition.Manual;
				this.Location = ConfigManager.Config.WindowLocation.Value;
			}

			Version currentVersion = new Version(InteropEmu.GetMesenVersion());
			lblVersion.Text = currentVersion.ToString();

			_fonts.AddFontFile(Path.Combine(ConfigManager.HomeFolder, "Resources", "PixelFont.ttf"));
			lblVersion.Font = new Font(_fonts.Families[0], 11);

			_commandLineArgs = args;
			
			Application.AddMessageFilter(this);
			this.Resize += ResizeRecentGames;
			this.FormClosed += (s, e) => Application.RemoveMessageFilter(this);
		}

		public void ProcessCommandLineArguments(string[] args, bool forStartup)
		{
			var switches = new List<string>();
			for(int i = 0; i < args.Length; i++) {
				if(args[i] != null) {
					switches.Add(args[i].ToLowerInvariant().Replace("--", "/").Replace("-", "/").Replace("=/", "=-"));
				}
			}

			if(forStartup) {
				_noVideo = switches.Contains("/novideo");
				_noAudio = switches.Contains("/noaudio");
				_noInput = switches.Contains("/noinput");
			}

			if(switches.Contains("/fullscreen")) {
				this.SetFullscreenState(true);
			}
			if(switches.Contains("/donotsavesettings")) {
				ConfigManager.DoNotSaveSettings = true;
			}
			ConfigManager.ProcessSwitches(switches);
		}

		public void LoadGameFromCommandLine(string[] args)
		{
			if(args.Length > 0) {
				foreach(string arg in args) {
					if(arg != null) {
						string path = arg;
						try {
							if(File.Exists(path)) {
								this.LoadFile(path);
								break;
							}

							//Try loading file as a relative path to the folder Mesen was started from
							path = Path.Combine(Program.OriginalFolder, path);
							if(File.Exists(path)) {
								this.LoadFile(path);
								break;
							}
						} catch { }
					}
				}
			}
		}

		protected override void OnLoad(EventArgs e)
		{
			base.OnLoad(e);

			#if HIDETESTMENU
			mnuTests.Visible = false;
			#endif

			_notifListener = new InteropEmu.NotificationListener();
			_notifListener.OnNotification += _notifListener_OnNotification;

			menuTimer.Start();
			
			this.ProcessCommandLineArguments(_commandLineArgs, true);

			VideoInfo.ApplyConfig();
			InitializeVsSystemMenu();
			InitializeFdsDiskMenu();
			InitializeEmulationSpeedMenu();
			
			UpdateVideoSettings();

			InitializeEmu();

			UpdateMenus();
			UpdateRecentFiles();

			UpdateViewerSize();

			if(ConfigManager.Config.PreferenceInfo.CloudSaveIntegration) {
				Task.Run(() => CloudSyncHelper.Sync());
			}

			this.LoadGameFromCommandLine(_commandLineArgs);

			if(ConfigManager.Config.PreferenceInfo.AutomaticallyCheckForUpdates) {
				CheckForUpdates(false);
			}
		}
		
		protected override void OnDeactivate(EventArgs e)
		{
			base.OnDeactivate(e);
			_removeFocus = true;
			InteropEmu.ResetKeyState();
		}

		protected override void OnActivated(EventArgs e)
		{
			base.OnActivated(e);
			_removeFocus = false;
			InteropEmu.ResetKeyState();
		}

		protected override void OnShown(EventArgs e)
		{
			base.OnShown(e);

			PerformUpgrade();
		}

		protected override void OnClosing(CancelEventArgs e)
		{
			if(_notifListener != null) {
				_notifListener.Dispose();
				_notifListener = null;
			}
			if(_debugger != null) {
				_debugger.Close();
			}

			ConfigManager.Config.EmulationInfo.EmulationSpeed = InteropEmu.GetEmulationSpeed();
			ConfigManager.Config.VideoInfo.VideoScale = _regularScale;
			if(this.WindowState == FormWindowState.Normal) {
				ConfigManager.Config.WindowLocation = this.Location;
			} else {
				ConfigManager.Config.WindowLocation = this.RestoreBounds.Location;
			}
			ConfigManager.ApplyChanges();

			StopEmu();

			if(ConfigManager.Config.PreferenceInfo.CloudSaveIntegration) {
				CloudSyncHelper.Sync();
			}

			InteropEmu.Release();

			ConfigManager.SaveConfig();

			base.OnClosing(e);
		}

		void PerformUpgrade()
		{
			Version newVersion = new Version(InteropEmu.GetMesenVersion());
			Version oldVersion = new Version(ConfigManager.Config.MesenVersion);
			if(oldVersion < newVersion) {
				//Upgrade
				if(oldVersion <= new Version("5.3.0")) {
					//Version 0.5.3-
					//Reduce sound latency if still using default
					if(ConfigManager.Config.AudioInfo.AudioLatency == 100) {
						//50ms is a fairly safe number - seems to work fine as low as 20ms (varies by computer)
						ConfigManager.Config.AudioInfo.AudioLatency = 50;
					}
				}

				if(oldVersion <= new Version("0.4.1")) {
					//Version 0.4.1-
					//Remove all old cheats (Game matching/CRC logic has been changed and no longer compatible)
					ConfigManager.Config.Cheats = new List<CheatInfo>();
				}

				if(oldVersion <= new Version("0.3.0")) {
					//Version 0.3.0-
					//Remove all old VS system config to make sure the new defaults are used
					ConfigManager.Config.VsConfig = new List<VsConfigInfo>();
				}

				ConfigManager.Config.MesenVersion = InteropEmu.GetMesenVersion();
				ConfigManager.ApplyChanges();

				MesenMsgBox.Show("UpgradeSuccess", MessageBoxButtons.OK, MessageBoxIcon.Information);
			}
		}

		private void menuTimer_Tick(object sender, EventArgs e)
		{
			this.UpdateMenus();
		}

		void InitializeEmu()
		{
			InteropEmu.InitializeEmu(ConfigManager.HomeFolder, this.Handle, this.ctrlRenderer.Handle, _noAudio, _noVideo, _noInput);
			foreach(RecentItem recentItem in ConfigManager.Config.RecentFiles) {
				InteropEmu.AddKnownGameFolder(Path.GetDirectoryName(recentItem.Path));
			}

			ConfigManager.Config.InitializeDefaults();
			ConfigManager.ApplyChanges();

			ConfigManager.Config.ApplyConfig();
		
			UpdateEmulationFlags();
		}

		private void InitializeEmulationSpeedMenu()
		{
			mnuEmuSpeedNormal.Tag = 100;
			mnuEmuSpeedTriple.Tag = 300;
			mnuEmuSpeedDouble.Tag = 200;
			mnuEmuSpeedHalf.Tag = 50;
			mnuEmuSpeedQuarter.Tag = 25;
			mnuEmuSpeedMaximumSpeed.Tag = 0;
		}

		private void UpdateEmulationSpeedMenu()
		{
			ConfigManager.Config.EmulationInfo.EmulationSpeed = InteropEmu.GetEmulationSpeed();
			foreach(ToolStripMenuItem item in new ToolStripMenuItem[] { mnuEmuSpeedDouble, mnuEmuSpeedHalf, mnuEmuSpeedNormal, mnuEmuSpeedQuarter, mnuEmuSpeedTriple, mnuEmuSpeedMaximumSpeed }) {
				item.Checked = ((int)item.Tag == ConfigManager.Config.EmulationInfo.EmulationSpeed);
			}
		}

		private void SetEmulationSpeed(uint emulationSpeed)
		{
			ConfigManager.Config.EmulationInfo.EmulationSpeed = emulationSpeed;
			ConfigManager.ApplyChanges();
			EmulationInfo.ApplyConfig();
		}

		private void mnuEmulationSpeed_DropDownOpening(object sender, EventArgs e)
		{
			UpdateEmulationSpeedMenu();
		}

		private void mnuIncreaseSpeed_Click(object sender, EventArgs e)
		{
			InteropEmu.IncreaseEmulationSpeed();
		}

		private void mnuDecreaseSpeed_Click(object sender, EventArgs e)
		{
			InteropEmu.DecreaseEmulationSpeed();
		}

		private void mnuEmuSpeedMaximumSpeed_Click(object sender, EventArgs e)
		{
			if(ConfigManager.Config.EmulationInfo.EmulationSpeed == 0) {
				SetEmulationSpeed(100);
			} else {
				SetEmulationSpeed(0);
			}
		}

		private void mnuEmulationSpeedOption_Click(object sender, EventArgs e)
		{
			SetEmulationSpeed((uint)(int)((ToolStripItem)sender).Tag);
		}

		private void UpdateEmulationFlags()
		{
			ConfigManager.Config.VideoInfo.ShowFPS = mnuShowFPS.Checked;
			ConfigManager.ApplyChanges();

			VideoInfo.ApplyConfig();
		}

		private void UpdateVideoSettings()
		{
			mnuShowFPS.Checked = ConfigManager.Config.VideoInfo.ShowFPS;
			mnuBilinearInterpolation.Checked = ConfigManager.Config.VideoInfo.UseBilinearInterpolation;
			UpdateScaleMenu(ConfigManager.Config.VideoInfo.VideoScale);
			UpdateFilterMenu(ConfigManager.Config.VideoInfo.VideoFilter);

			_customSize = false;
			UpdateViewerSize();
		}

		private void UpdateViewerSize()
		{
			InteropEmu.ScreenSize size = InteropEmu.GetScreenSize(false);

			if(!_customSize && this.WindowState != FormWindowState.Maximized) {
				Size sizeGap = this.Size - this.ClientSize;

				_regularScale = size.Scale;
				UpdateScaleMenu(size.Scale);

				this.Resize -= frmMain_Resize;
				this.ClientSize = new Size(Math.Max(this.MinimumSize.Width - sizeGap.Width, size.Width), Math.Max(this.MinimumSize.Height - sizeGap.Height, size.Height + (this.HideMenuStrip ? 0 : menuStrip.Height)));
				this.Resize += frmMain_Resize;
			}

			ctrlRenderer.Size = new Size(size.Width, size.Height);
			ctrlRenderer.Left = (panelRenderer.Width - ctrlRenderer.Width) / 2;
			ctrlRenderer.Top = (panelRenderer.Height - ctrlRenderer.Height) / 2;

			if(this.HideMenuStrip) {
				this.menuStrip.Visible = false;
			}
		}

		private void ResizeRecentGames(object sender, EventArgs e)
		{
			if(this.ClientSize.Height < 400) {
				ctrlRecentGames.Height = this.ClientSize.Height - 125 + Math.Min(50, (400 - this.ClientSize.Height));
			} else {
				ctrlRecentGames.Height = this.ClientSize.Height - 125;
			}
		}

		private void frmMain_Resize(object sender, EventArgs e)
		{
			if(this.WindowState != FormWindowState.Minimized) {
				SetScaleBasedOnWindowSize();
				ctrlRenderer.Left = (panelRenderer.Width - ctrlRenderer.Width) / 2;
				ctrlRenderer.Top = (panelRenderer.Height - ctrlRenderer.Height) / 2;
			}
		}
		
		private void SetScaleBasedOnWindowSize()
		{
			_customSize = true;
			InteropEmu.ScreenSize size = InteropEmu.GetScreenSize(true);
			double verticalScale = (double)panelRenderer.ClientSize.Height / size.Height;
			double horizontalScale = (double)panelRenderer.ClientSize.Width / size.Width;
			double scale = Math.Min(verticalScale, horizontalScale);
			UpdateScaleMenu(scale);
			VideoInfo.ApplyConfig();
		}

		private void SetFullscreenState(bool enabled)
		{
			this.Resize -= frmMain_Resize;
			_fullscreenMode = enabled;
			if(enabled) {
				this.menuStrip.Visible = false;
				_originalWindowState = this.WindowState;
				this.WindowState = FormWindowState.Normal;
				this.FormBorderStyle = FormBorderStyle.None;
				this.WindowState = FormWindowState.Maximized;
				SetScaleBasedOnWindowSize();
			} else {
				this.menuStrip.Visible = true;
				this.WindowState = _originalWindowState;
				this.FormBorderStyle = FormBorderStyle.Sizable;
				this.SetScale(_regularScale);
				this.UpdateScaleMenu(_regularScale);
				this.UpdateViewerSize();
				VideoInfo.ApplyConfig();				
			}
			this.Resize += frmMain_Resize;
			mnuFullscreen.Checked = enabled;
		}

		private bool HideMenuStrip
		{
			get
			{
				return _fullscreenMode || ConfigManager.Config.PreferenceInfo.AutoHideMenu;
			}
		}

		private void ctrlRenderer_MouseMove(object sender, MouseEventArgs e)
		{
			if(this.HideMenuStrip && !this.menuStrip.ContainsFocus) {
				if(sender == ctrlRenderer) {
					this.menuStrip.Visible = ctrlRenderer.Top + e.Y < 30;
				} else {
					this.menuStrip.Visible = e.Y < 30;
				}
			}
		}

		private void ctrlRenderer_MouseClick(object sender, MouseEventArgs e)
		{
			if(this.HideMenuStrip) {
				this.menuStrip.Visible = false;
			}
		}

		private void _notifListener_OnNotification(InteropEmu.NotificationEventArgs e)
		{
			switch(e.NotificationType) {
				case InteropEmu.ConsoleNotificationType.GameLoaded:
					_currentGame = InteropEmu.GetRomInfo().GetRomName();
					InteropEmu.SetNesModel(ConfigManager.Config.Region);
					InitializeNsfMode(false, true);
					InitializeFdsDiskMenu();
					InitializeVsSystemMenu();
					CheatInfo.ApplyCheats();
					VsConfigInfo.ApplyConfig();
					InitializeStateMenu(mnuSaveState, true);
					InitializeStateMenu(mnuLoadState, false);
					if(ConfigManager.Config.PreferenceInfo.ShowVsConfigOnLoad && InteropEmu.IsVsSystem()) {
						this.Invoke((MethodInvoker)(() => {
							this.ShowVsGameConfig();
						}));
					}

					this.StartEmuThread();
					this.BeginInvoke((MethodInvoker)(() => {
						UpdateViewerSize();
					}));
					break;

				case InteropEmu.ConsoleNotificationType.PpuFrameDone:
					if(InteropEmu.IsNsf()) {
						this.ctrlNsfPlayer.CountFrame();
					}
					break;

				case InteropEmu.ConsoleNotificationType.GameReset:
					InitializeNsfMode();
					break;

				case InteropEmu.ConsoleNotificationType.DisconnectedFromServer:
					ConfigManager.Config.ApplyConfig();
					break;

				case InteropEmu.ConsoleNotificationType.GameStopped:
					this._currentGame = null;
					CheatInfo.ClearCheats();
					this.BeginInvoke((MethodInvoker)(() => {
						ctrlRecentGames.Initialize();
					}));
					break;

				case InteropEmu.ConsoleNotificationType.ResolutionChanged:
					this.BeginInvoke((MethodInvoker)(() => {
						UpdateViewerSize();
					}));
					break;

				case InteropEmu.ConsoleNotificationType.FdsBiosNotFound:
					this.BeginInvoke((MethodInvoker)(() => {
						SelectFdsBiosPrompt();
					}));
					break;

				case InteropEmu.ConsoleNotificationType.RequestExit:
					this.BeginInvoke((MethodInvoker)(() => this.Close()));
					break;

				case InteropEmu.ConsoleNotificationType.ToggleCheats:
					this.BeginInvoke((MethodInvoker)(() => {
						ConfigManager.Config.DisableAllCheats = !ConfigManager.Config.DisableAllCheats;
						if(ConfigManager.Config.DisableAllCheats) {
							InteropEmu.DisplayMessage("Cheats", "CheatsDisabled");
						}
						CheatInfo.ApplyCheats();
						ConfigManager.ApplyChanges();
					}));
					break;

				case InteropEmu.ConsoleNotificationType.ToggleAudio:
					this.BeginInvoke((MethodInvoker)(() => {
						ConfigManager.Config.AudioInfo.EnableAudio = !ConfigManager.Config.AudioInfo.EnableAudio;
						AudioInfo.ApplyConfig();
						ConfigManager.ApplyChanges();
					}));
					break;
			}

			if(e.NotificationType != InteropEmu.ConsoleNotificationType.PpuFrameDone) {
				UpdateMenus();
			}
		}

		private void mnuOpen_Click(object sender, EventArgs e)
		{
			OpenFileDialog ofd = new OpenFileDialog();
			ofd.SetFilter(ResourceHelper.GetMessage("FilterRomIps"));
			if(ConfigManager.Config.RecentFiles.Count > 0) {
				ofd.InitialDirectory = Path.GetDirectoryName(ConfigManager.Config.RecentFiles[0].Path);
			}			
			if(ofd.ShowDialog() == System.Windows.Forms.DialogResult.OK) {
				LoadFile(ofd.FileName);
			}
		}

		private bool IsPatchFile(string filename)
		{
			using(FileStream stream = File.OpenRead(filename)) {
				byte[] header = new byte[5];
				stream.Read(header, 0, 5);
				if(header[0] == 'P' && header[1] == 'A' && header[2] == 'T' && header[3] == 'C' && header[4] == 'H') {
					return true;
				} else if((header[0] == 'U' || header[0] == 'B') && header[1] == 'P' && header[2] == 'S' && header[3] == '1') {
					return true;
				}
			}
			return false;
		}

		private void LoadFile(string filename)
		{
			if(File.Exists(filename)) {
				if(IsPatchFile(filename)) {
					LoadPatchFile(filename);
				} else if(Path.GetExtension(filename).ToLowerInvariant() == ".mmo") {
					InteropEmu.MoviePlay(filename);
				} else {
					LoadROM(filename, ConfigManager.Config.PreferenceInfo.AutoLoadIpsPatches);
				}
			}
		}

		private void LoadPatchFile(string patchFile)
		{
			if(_emuThread == null) {
				if(MesenMsgBox.Show("SelectRomIps", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == System.Windows.Forms.DialogResult.OK) {
					OpenFileDialog ofd = new OpenFileDialog();
					ofd.SetFilter(ResourceHelper.GetMessage("FilterRom"));
					if(ConfigManager.Config.RecentFiles.Count > 0) {
						ofd.InitialDirectory = Path.GetDirectoryName(ConfigManager.Config.RecentFiles[0].Path);
					}

					if(ofd.ShowDialog() == System.Windows.Forms.DialogResult.OK) {
						LoadROM(ofd.FileName, true, -1, patchFile);
					}					
				}
			} else if(MesenMsgBox.Show("PatchAndReset", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == System.Windows.Forms.DialogResult.OK) {
				LoadROM(_currentRomPath, true, _currentRomArchiveIndex, patchFile);
			}
		}

		private void LoadROM(string filename, bool autoLoadPatches = false, int archiveFileIndex = -1, string patchFileToApply = null)
		{
			_currentRomPath = filename;
			_currentRomArchiveIndex = -1;
			if(File.Exists(filename)) {
				string romName;
				if(frmSelectRom.SelectRom(filename, ref archiveFileIndex, out romName)) {
					_currentRomArchiveIndex = archiveFileIndex;
					if(archiveFileIndex >= 0) {
						Interlocked.Increment(ref _romLoadCounter);
						ctrlNsfPlayer.Visible = false;
						ctrlLoading.Visible = true;
					}

					string patchFile = patchFileToApply;
					if(patchFile == null) {
						string[] extensions = new string[3] { ".ips", ".ups", ".bps" };
						foreach(string ext in extensions) {
							string file = Path.Combine(Path.GetDirectoryName(filename), Path.GetFileNameWithoutExtension(filename)) + ext;
							if(File.Exists(file)) {
								patchFile = file;
								break;
							}
						}
					}
					
					if(!File.Exists(patchFile)) {
						autoLoadPatches = false;
					}

					Task loadRomTask = new Task(() => {
						lock(_loadRomLock) {
							InteropEmu.LoadROM(filename, archiveFileIndex, autoLoadPatches ? patchFile : string.Empty);
						}
					});

					loadRomTask.ContinueWith((Task prevTask) => {
						this.BeginInvoke((MethodInvoker)(() => {
							if(archiveFileIndex >= 0) {
								Interlocked.Decrement(ref _romLoadCounter);
							}

							ConfigManager.Config.AddRecentFile(filename, romName, archiveFileIndex);
							UpdateRecentFiles();
						}));
					});

					loadRomTask.Start();
				}
			} else {
				MesenMsgBox.Show("FileNotFound", MessageBoxButtons.OK, MessageBoxIcon.Error, filename);
			}
		}
		
		private void UpdateFocusFlag()
		{
			bool hasFocus = false;
			if(Application.OpenForms.Count > 0) {
				if(Application.OpenForms[0].ContainsFocus) {
					hasFocus = true;
				}
			}

			if(_removeFocus) {
				hasFocus = false;
			}

			InteropEmu.SetFlag(EmulationFlags.InBackground, !hasFocus);
		}

		Image _pauseButton = Mesen.GUI.Properties.Resources.Pause;
		Image _playButton = Mesen.GUI.Properties.Resources.Play;
		private void UpdateMenus()
		{
			try {
				if(this.InvokeRequired) {
					this.BeginInvoke((MethodInvoker)(() => this.UpdateMenus()));
				} else {
					panelInfo.Visible = _emuThread == null;
					ctrlRecentGames.Visible = _emuThread == null;
					mnuPowerOff.Enabled = _emuThread != null;

					ctrlLoading.Visible = (_romLoadCounter > 0);

					UpdateFocusFlag();
					UpdateWindowTitle();

					bool isNetPlayClient = InteropEmu.IsConnected();

					mnuPause.Enabled = mnuPowerCycle.Enabled = mnuReset.Enabled = (_emuThread != null && !isNetPlayClient);
					mnuSaveState.Enabled = (_emuThread != null && !isNetPlayClient && !InteropEmu.IsNsf());
					mnuLoadState.Enabled = (_emuThread != null && !isNetPlayClient && !InteropEmu.IsNsf() && !InteropEmu.MoviePlaying() && !InteropEmu.MovieRecording());

					//Disable pause when debugger is running
					mnuPause.Enabled &= !InteropEmu.DebugIsDebuggerRunning();

					mnuPause.Text = InteropEmu.IsPaused() ? ResourceHelper.GetMessage("Resume") : ResourceHelper.GetMessage("Pause");
					mnuPause.Image = InteropEmu.IsPaused() ? _playButton : _pauseButton;

					bool netPlay = InteropEmu.IsServerRunning() || isNetPlayClient;

					mnuStartServer.Enabled = !isNetPlayClient;
					mnuConnect.Enabled = !InteropEmu.IsServerRunning();
					mnuNetPlaySelectController.Enabled = isNetPlayClient || InteropEmu.IsServerRunning();
					if(mnuNetPlaySelectController.Enabled) {
						int availableControllers = InteropEmu.NetPlayGetAvailableControllers();
						int currentControllerPort = InteropEmu.NetPlayGetControllerPort();
						mnuNetPlayPlayer1.Enabled = (availableControllers & 0x01) == 0x01;
						mnuNetPlayPlayer2.Enabled = (availableControllers & 0x02) == 0x02;
						mnuNetPlayPlayer3.Enabled = (availableControllers & 0x04) == 0x04;
						mnuNetPlayPlayer4.Enabled = (availableControllers & 0x08) == 0x08;
						mnuNetPlayPlayer1.Text = ResourceHelper.GetMessage("PlayerNumber", "1") + " (" + ResourceHelper.GetEnumText(InteropEmu.NetPlayGetControllerType(0)) + ")";
						mnuNetPlayPlayer2.Text = ResourceHelper.GetMessage("PlayerNumber", "2") + " (" + ResourceHelper.GetEnumText(InteropEmu.NetPlayGetControllerType(1)) + ")";
						mnuNetPlayPlayer3.Text = ResourceHelper.GetMessage("PlayerNumber", "3") + " (" + ResourceHelper.GetEnumText(InteropEmu.NetPlayGetControllerType(2)) + ")";
						mnuNetPlayPlayer4.Text = ResourceHelper.GetMessage("PlayerNumber", "4") + " (" + ResourceHelper.GetEnumText(InteropEmu.NetPlayGetControllerType(3)) + ")";

						mnuNetPlayPlayer1.Checked = (currentControllerPort == 0);
						mnuNetPlayPlayer2.Checked = (currentControllerPort == 1);
						mnuNetPlayPlayer3.Checked = (currentControllerPort == 2);
						mnuNetPlayPlayer4.Checked = (currentControllerPort == 3);
						mnuNetPlaySpectator.Checked = (currentControllerPort == 0xFF);

						mnuNetPlaySpectator.Enabled = true;
					}

					mnuStartServer.Text = InteropEmu.IsServerRunning() ? ResourceHelper.GetMessage("StopServer") : ResourceHelper.GetMessage("StartServer");
					mnuConnect.Text = isNetPlayClient ? ResourceHelper.GetMessage("Disconnect") : ResourceHelper.GetMessage("ConnectToServer");

					mnuCheats.Enabled = !isNetPlayClient;
					mnuEmulationSpeed.Enabled = !isNetPlayClient;
					mnuIncreaseSpeed.Enabled = !isNetPlayClient;
					mnuDecreaseSpeed.Enabled = !isNetPlayClient;
					mnuEmuSpeedMaximumSpeed.Enabled = !isNetPlayClient;
					mnuInput.Enabled = !isNetPlayClient;
					mnuRegion.Enabled = !isNetPlayClient;

					bool moviePlaying = InteropEmu.MoviePlaying();
					bool movieRecording = InteropEmu.MovieRecording();
					mnuPlayMovie.Enabled = !netPlay && !moviePlaying && !movieRecording;
					mnuStopMovie.Enabled = _emuThread != null && !netPlay && (moviePlaying || movieRecording);
					mnuRecordFrom.Enabled = _emuThread != null && !moviePlaying && !movieRecording;
					mnuRecordFromStart.Enabled = _emuThread != null && !isNetPlayClient && !moviePlaying && !movieRecording;
					mnuRecordFromNow.Enabled = _emuThread != null && !moviePlaying && !movieRecording;

					bool waveRecording = InteropEmu.WaveIsRecording();
					mnuWaveRecord.Enabled = _emuThread != null && !waveRecording;
					mnuWaveStop.Enabled = _emuThread != null && waveRecording;

					bool aviRecording = InteropEmu.AviIsRecording();
					mnuAviRecord.Enabled = _emuThread != null && !aviRecording;
					mnuAviStop.Enabled = _emuThread != null && aviRecording;
					mnuVideoRecorder.Enabled = !_isNsfPlayerMode;

					bool testRecording = InteropEmu.RomTestRecording();
					mnuTestRun.Enabled = !netPlay && !moviePlaying && !movieRecording;
					mnuTestStopRecording.Enabled = _emuThread != null && testRecording;
					mnuTestRecordStart.Enabled = _emuThread != null && !isNetPlayClient && !moviePlaying && !movieRecording;
					mnuTestRecordNow.Enabled = _emuThread != null && !moviePlaying && !movieRecording;
					mnuTestRecordMovie.Enabled = !netPlay && !moviePlaying && !movieRecording;
					mnuTestRecordTest.Enabled = !netPlay && !moviePlaying && !movieRecording;
					mnuTestRecordFrom.Enabled = (mnuTestRecordStart.Enabled || mnuTestRecordNow.Enabled || mnuTestRecordMovie.Enabled || mnuTestRecordTest.Enabled);

					mnuDebugger.Enabled = !netPlay && _emuThread != null;

					mnuTakeScreenshot.Enabled = _emuThread != null && !InteropEmu.IsNsf();
					mnuNetPlay.Enabled = !InteropEmu.IsNsf();
					if(_emuThread != null && InteropEmu.IsNsf()) {
						mnuPowerCycle.Enabled = false;
						mnuMovies.Enabled = mnuPlayMovie.Enabled = mnuStopMovie.Enabled = mnuRecordFrom.Enabled = mnuRecordFromStart.Enabled = mnuRecordFromNow.Enabled = false;
					}

					mnuRegionAuto.Checked = ConfigManager.Config.Region == NesModel.Auto;
					mnuRegionNtsc.Checked = ConfigManager.Config.Region == NesModel.NTSC;
					mnuRegionPal.Checked = ConfigManager.Config.Region == NesModel.PAL;
					mnuRegionDendy.Checked = ConfigManager.Config.Region == NesModel.Dendy;

					bool autoInsertDisabled = !InteropEmu.FdsIsAutoInsertDiskEnabled(); 
					mnuSelectDisk.Enabled = autoInsertDisabled;
					mnuEjectDisk.Enabled = autoInsertDisabled;
					mnuSwitchDiskSide.Enabled = autoInsertDisabled;
				}
			} catch { }
		}

		private void UpdateWindowTitle()
		{
			string title = "Mesen";
			if(!string.IsNullOrWhiteSpace(_currentGame)) {
				title += " - " + _currentGame;
			}
			if(ConfigManager.Config.PreferenceInfo.DisplayTitleBarInfo) {
				title += string.Format(" - {0}x{1} ({2:0.##}x, {3}) - {4}", ctrlRenderer.Width, ctrlRenderer.Height, ConfigManager.Config.VideoInfo.VideoScale, ResourceHelper.GetEnumText(ConfigManager.Config.VideoInfo.AspectRatio), ResourceHelper.GetEnumText(ConfigManager.Config.VideoInfo.VideoFilter));
			}
			this.Text = title;
		}

		private void UpdateRecentFiles()
		{
			mnuRecentFiles.DropDownItems.Clear();
			foreach(RecentItem recentItem in ConfigManager.Config.RecentFiles) {
				ToolStripMenuItem tsmi = new ToolStripMenuItem();
				tsmi.Text = recentItem.RomName.Replace("&", "&&");
				tsmi.Click += (object sender, EventArgs args) => {
					LoadROM(recentItem.Path, ConfigManager.Config.PreferenceInfo.AutoLoadIpsPatches, recentItem.ArchiveFileIndex);
				};
				mnuRecentFiles.DropDownItems.Add(tsmi);
			}

			mnuRecentFiles.Enabled = mnuRecentFiles.DropDownItems.Count > 0;
		}

		private void StartEmuThread()
		{
			if(_emuThread == null) {
				_emuThread = new Thread(() => {
					try {
						InteropEmu.Run();
						_emuThread = null;
					} catch(Exception ex) {
						MesenMsgBox.Show("UnexpectedError", MessageBoxButtons.OK, MessageBoxIcon.Error, ex.ToString());
					}
				});
				_emuThread.Start();
			}
			UpdateMenus();
		}
				
		private void StopEmu()
		{
			InteropEmu.Stop();
		}

		private void PauseEmu()
		{
			if(InteropEmu.IsPaused()) {
				InteropEmu.Resume();
			} else {
				InteropEmu.Pause();
			}

			ctrlNsfPlayer.UpdateText();
		}

		private void ResetEmu()
		{
			InteropEmu.Reset();
		}

		const int WM_KEYDOWN = 0x100;
		const int WM_KEYUP = 0x101;

		bool IMessageFilter.PreFilterMessage(ref Message m)
		{
			if(m.Msg == WM_KEYUP) {
				int scanCode = (Int32)(((Int64)m.LParam & 0x1FF0000) >> 16);
				InteropEmu.SetKeyState(scanCode, false);
			}

			return false;
		}

		protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
		{
			if(msg.Msg == WM_KEYDOWN) {
				int scanCode = (Int32)(((Int64)msg.LParam & 0x1FF0000) >> 16);
				InteropEmu.SetKeyState(scanCode, true);
			}

			if(!this.menuStrip.Enabled) {
				//Make sure we disable all shortcut keys while the bar is disabled (i.e when running tests)
				return false;
			}

			if(this.HideMenuStrip && (keyData & Keys.Alt) == Keys.Alt) {
				if(this.menuStrip.Visible && !this.menuStrip.ContainsFocus) {
					this.menuStrip.Visible = false;
				} else {
					this.menuStrip.Visible = true;
					this.menuStrip.Focus();
				}
			}

			#if !HIDETESTMENU
			if(keyData == Keys.Pause) {
				if(InteropEmu.RomTestRecording()) {
					InteropEmu.RomTestStop();
				} else {
					InteropEmu.RomTestRecord(ConfigManager.TestFolder + "\\" + InteropEmu.GetRomInfo().GetRomName() + ".mtp", true);
				}
			}
			#endif

			if(keyData == Keys.Escape && _emuThread != null && mnuPause.Enabled) {
				PauseEmu();
				return true;
			} else if(keyData == Keys.Oemplus) {
				mnuIncreaseSpeed.PerformClick();
				return true;
			} else if(keyData == Keys.OemMinus) {
				mnuDecreaseSpeed.PerformClick();
				return true;
			}
			return base.ProcessCmdKey(ref msg, keyData);
		}

		const int NumberOfSaveSlots = 7;
		private void InitializeStateMenu(ToolStripMenuItem menu, bool forSave)
		{
			if(this.InvokeRequired) {
				this.BeginInvoke((MethodInvoker)(() => this.InitializeStateMenu(menu, forSave)));
			} else {
				menu.DropDownItems.Clear();

				Action<uint> addSaveStateInfo = (i) => {
					Int64 fileTime = InteropEmu.GetStateInfo(i);
					string label;
					if(fileTime == 0) {
						label = i.ToString() + ". " + ResourceHelper.GetMessage("EmptyState");
					} else {
						DateTime dateTime = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(fileTime).ToLocalTime();
						label = i.ToString() + ". " + dateTime.ToShortDateString() + " " + dateTime.ToShortTimeString();
					}

					ToolStripMenuItem item = new ToolStripMenuItem(label);
					uint stateIndex = i;
					item.Click += (object sender, EventArgs e) => {
						if(_emuThread != null && !InteropEmu.IsNsf()) {
							if(forSave) {
								InteropEmu.SaveState(stateIndex);
							} else {
								if(!InteropEmu.MoviePlaying() && !InteropEmu.MovieRecording()) {
									InteropEmu.LoadState(stateIndex);
								}
							}
						}
					};

					item.ShortcutKeys = (Keys)((int)Keys.F1 + i - 1);
					if(forSave) {
						item.ShortcutKeys |= Keys.Shift;
					}
					menu.DropDownItems.Add(item);
				};

				for(uint i = 1; i <= frmMain.NumberOfSaveSlots; i++) {
					addSaveStateInfo(i);
				}

				if(!forSave) {
					menu.DropDownItems.Add("-");
					addSaveStateInfo(NumberOfSaveSlots+1);
				}
			}
		}

		#region Events

		private void mnuPause_Click(object sender, EventArgs e)
		{
			PauseEmu();
		}

		private void mnuReset_Click(object sender, EventArgs e)
		{
			ResetEmu();
		}

		private void mnuPowerCycle_Click(object sender, EventArgs e)
		{
			InteropEmu.PowerCycle();
		}

		private void mnuPowerOff_Click(object sender, EventArgs e)
		{
			InteropEmu.Stop();
		}

		private void mnuShowFPS_Click(object sender, EventArgs e)
		{
			UpdateEmulationFlags();
		}

		private void mnuStartServer_Click(object sender, EventArgs e)
		{
			if(InteropEmu.IsServerRunning()) {
				Task.Run(() => InteropEmu.StopServer());
			} else {
				frmServerConfig frm = new frmServerConfig();
				if(frm.ShowDialog(sender) == System.Windows.Forms.DialogResult.OK) {
					InteropEmu.StartServer(ConfigManager.Config.ServerInfo.Port, ConfigManager.Config.Profile.PlayerName);
				}
			}
		}

		private void mnuConnect_Click(object sender, EventArgs e)
		{
			if(InteropEmu.IsConnected()) {
				Task.Run(() => InteropEmu.Disconnect());
			} else {
				frmClientConfig frm = new frmClientConfig();
				if(frm.ShowDialog(sender) == System.Windows.Forms.DialogResult.OK) {
					Task.Run(() => {
						InteropEmu.Connect(ConfigManager.Config.ClientConnectionInfo.Host, ConfigManager.Config.ClientConnectionInfo.Port, ConfigManager.Config.Profile.PlayerName, ConfigManager.Config.ClientConnectionInfo.Spectator);
					});
				}
			}
		}

		private void mnuProfile_Click(object sender, EventArgs e)
		{
			new frmPlayerProfile().ShowDialog(sender);
		}
		
		private void mnuExit_Click(object sender, EventArgs e)
		{
			this.Close();
		}
		
		private void mnuVideoConfig_Click(object sender, EventArgs e)
		{
			new frmVideoConfig().ShowDialog(sender);
			UpdateVideoSettings();
		}
		
		private void mnuDebugger_Click(object sender, EventArgs e)
		{
			if(_debugger == null) {
				_debugger = new frmDebugger();
				_debugger.FormClosed += (obj, args) => {
					_debugger = null;
				};
				_debugger.Show();
			} else {
				_debugger.Focus();
			}
		}
		
		private void mnuSaveState_DropDownOpening(object sender, EventArgs e)
		{
			InitializeStateMenu(mnuSaveState, true);
		}

		private void mnuLoadState_DropDownOpening(object sender, EventArgs e)
		{
			InitializeStateMenu(mnuLoadState, false);
		}

		private void mnuTakeScreenshot_Click(object sender, EventArgs e)
		{
			InteropEmu.TakeScreenshot();
		}

		#endregion
		
		private void RecordMovie(bool resetEmu)
		{
			SaveFileDialog sfd = new SaveFileDialog();
			sfd.SetFilter(ResourceHelper.GetMessage("FilterMovie"));
			sfd.InitialDirectory = ConfigManager.MovieFolder;
			sfd.FileName = InteropEmu.GetRomInfo().GetRomName() + ".mmo";
			if(sfd.ShowDialog() == System.Windows.Forms.DialogResult.OK) {
				InteropEmu.MovieRecord(sfd.FileName, resetEmu);
			}
		}

		private void mnuPlayMovie_Click(object sender, EventArgs e)
		{
			OpenFileDialog ofd = new OpenFileDialog();
			ofd.SetFilter(ResourceHelper.GetMessage("FilterMovie"));
			ofd.InitialDirectory = ConfigManager.MovieFolder;
			if(ofd.ShowDialog() == System.Windows.Forms.DialogResult.OK) {
				InteropEmu.MoviePlay(ofd.FileName);
			}
		}

		private void mnuStopMovie_Click(object sender, EventArgs e)
		{
			InteropEmu.MovieStop();
		}

		private void mnuRecordFromStart_Click(object sender, EventArgs e)
		{
			RecordMovie(true);
		}

		private void mnuRecordFromNow_Click(object sender, EventArgs e)
		{
			RecordMovie(false);
		}

		private void mnuWaveRecord_Click(object sender, EventArgs e)
		{
			SaveFileDialog sfd = new SaveFileDialog();
			sfd.SetFilter(ResourceHelper.GetMessage("FilterWave"));
			sfd.InitialDirectory = ConfigManager.WaveFolder;
			sfd.FileName = InteropEmu.GetRomInfo().GetRomName() + ".wav";
			if(sfd.ShowDialog() == System.Windows.Forms.DialogResult.OK) {
				InteropEmu.WaveRecord(sfd.FileName);
			}
		}

		private void mnuWaveStop_Click(object sender, EventArgs e)
		{
			InteropEmu.WaveStop();
		}

		private void mnuAviRecord_Click(object sender, EventArgs e)
		{
			using(frmRecordAvi frm = new frmRecordAvi()) {
				if(frm.ShowDialog(mnuVideoRecorder) == DialogResult.OK) {
					InteropEmu.AviRecord(frm.Filename, ConfigManager.Config.AviRecordInfo.Codec, ConfigManager.Config.AviRecordInfo.CompressionLevel);
				}
			}
		}

		private void mnuAviStop_Click(object sender, EventArgs e)
		{
			InteropEmu.AviStop();
		}

		private void mnuTestRun_Click(object sender, EventArgs e)
		{
			OpenFileDialog ofd = new OpenFileDialog();
			ofd.SetFilter(ResourceHelper.GetMessage("FilterTest"));
			ofd.InitialDirectory = ConfigManager.TestFolder;
			ofd.Multiselect = true;
			if(ofd.ShowDialog() == System.Windows.Forms.DialogResult.OK) {
				List<string> passedTests = new List<string>();
				List<string> failedTests = new List<string>();
				List<int> failedFrameCount = new List<int>();

				this.menuStrip.Enabled = false;

				Task.Run(() => {
					foreach(string filename in ofd.FileNames) {
						int result = InteropEmu.RunRecordedTest(filename);

						if(result == 0) {
							passedTests.Add(Path.GetFileNameWithoutExtension(filename));
						} else {
							failedTests.Add(Path.GetFileNameWithoutExtension(filename));
							failedFrameCount.Add(result);
						}
					}

					this.BeginInvoke((MethodInvoker)(() => {
						if(failedTests.Count == 0) {
							MessageBox.Show("All tests passed.", "", MessageBoxButtons.OK, MessageBoxIcon.Information);
						} else {
							StringBuilder message = new StringBuilder();
							if(passedTests.Count > 0) {
								message.AppendLine("Passed tests:");
								foreach(string test in passedTests) {
									message.AppendLine("  -" + test);
								}
								message.AppendLine("");
							}
							message.AppendLine("Failed tests:");
							for(int i = 0, len = failedTests.Count; i < len; i++) {
								message.AppendLine("  -" + failedTests[i] + " (" + failedFrameCount[i] + ")");
							}
							MessageBox.Show(message.ToString(), "", MessageBoxButtons.OK, MessageBoxIcon.Error);
						}

						this.menuStrip.Enabled = true;
					}));
				});
			}
		}

		private void mnuTestRecordStart_Click(object sender, EventArgs e)
		{
			RecordTest(true);
		}

		private void mnuTestRecordNow_Click(object sender, EventArgs e)
		{
			RecordTest(false);
		}

		private void RecordTest(bool resetEmu)
		{
			SaveFileDialog sfd = new SaveFileDialog();
			sfd.SetFilter(ResourceHelper.GetMessage("FilterTest"));
			sfd.InitialDirectory = ConfigManager.TestFolder;
			sfd.FileName = InteropEmu.GetRomInfo().GetRomName() + ".mtp";
			if(sfd.ShowDialog() == System.Windows.Forms.DialogResult.OK) {
				InteropEmu.RomTestRecord(sfd.FileName, resetEmu);
			}
		}

		private void mnuTestRecordMovie_Click(object sender, EventArgs e)
		{
			OpenFileDialog ofd = new OpenFileDialog();
			ofd.SetFilter(ResourceHelper.GetMessage("FilterMovie"));
			ofd.InitialDirectory = ConfigManager.MovieFolder;
			if(ofd.ShowDialog() == System.Windows.Forms.DialogResult.OK) {
				SaveFileDialog sfd = new SaveFileDialog();
				sfd.SetFilter(ResourceHelper.GetMessage("FilterTest"));
				sfd.InitialDirectory = ConfigManager.TestFolder;
				sfd.FileName = Path.GetFileNameWithoutExtension(ofd.FileName) + ".mtp";
				if(sfd.ShowDialog() == System.Windows.Forms.DialogResult.OK) {
					InteropEmu.RomTestRecordFromMovie(sfd.FileName, ofd.FileName);
				}
			}
		}

		private void mnuTestRecordTest_Click(object sender, EventArgs e)
		{
			OpenFileDialog ofd = new OpenFileDialog();
			ofd.SetFilter(ResourceHelper.GetMessage("FilterTest"));
			ofd.InitialDirectory = ConfigManager.TestFolder;

			if(ofd.ShowDialog() == System.Windows.Forms.DialogResult.OK) {
				SaveFileDialog sfd = new SaveFileDialog();
				sfd.SetFilter(ResourceHelper.GetMessage("FilterTest"));
				sfd.InitialDirectory = ConfigManager.TestFolder;
				sfd.FileName = Path.GetFileNameWithoutExtension(ofd.FileName) + ".mtp";
				if(sfd.ShowDialog() == System.Windows.Forms.DialogResult.OK) {
					InteropEmu.RomTestRecordFromTest(sfd.FileName, ofd.FileName);
				}
			}
		}

		private void mnuTestStopRecording_Click(object sender, EventArgs e)
		{
			InteropEmu.RomTestStop();
		}

		private void mnuCheats_Click(object sender, EventArgs e)
		{
			if(_cheatListWindow == null) {
				_cheatListWindow = new frmCheatList();
				_cheatListWindow.Show(sender, this);
				_cheatListWindow.FormClosed += (s, evt) => {
					if(_cheatListWindow.DialogResult == DialogResult.OK) {
						CheatInfo.ApplyCheats();
					}
					_cheatListWindow = null;					
				};
			} else {
				_cheatListWindow.Focus();
			}
		}

		private void mnuInput_Click(object sender, EventArgs e)
		{
			new frmInputConfig().ShowDialog(sender);
		}

		private void mnuAudioConfig_Click(object sender, EventArgs e)
		{
			new frmAudioConfig().ShowDialog(sender);
			this.ctrlNsfPlayer.UpdateVolume();
		}

		private void mnuPreferences_Click(object sender, EventArgs e)
		{
			if(new frmPreferences().ShowDialog(sender) == DialogResult.OK) {
				ResourceHelper.LoadResources(ConfigManager.Config.PreferenceInfo.DisplayLanguage);
				ResourceHelper.UpdateEmuLanguage();
				ResourceHelper.ApplyResources(this);
				UpdateMenus();
				InitializeFdsDiskMenu();
				InitializeNsfMode(true);
				ctrlRecentGames.UpdateGameInfo();
			} else {
				UpdateVideoSettings();
				UpdateMenus();
				UpdateRecentFiles();
				UpdateViewerSize();
			}
		}

		private void mnuRegion_Click(object sender, EventArgs e)
		{
			if(sender == mnuRegionAuto) {
				ConfigManager.Config.Region = NesModel.Auto;
			} else if(sender == mnuRegionNtsc) {
				ConfigManager.Config.Region = NesModel.NTSC;
			} else if(sender == mnuRegionPal) {
				ConfigManager.Config.Region = NesModel.PAL;
			} else if(sender == mnuRegionDendy) {
				ConfigManager.Config.Region = NesModel.Dendy;
			}
			InteropEmu.SetNesModel(ConfigManager.Config.Region);
		}

		private void mnuRunAutomaticTest_Click(object sender, EventArgs e)
		{
			using(OpenFileDialog ofd = new OpenFileDialog()) {
				ofd.SetFilter("*.nes|*.nes");
				if(ofd.ShowDialog() == System.Windows.Forms.DialogResult.OK) {
					string filename = ofd.FileName;
					
					Task.Run(() => {
						int result = InteropEmu.RunAutomaticTest(filename);
					});
				}
			}
		}

		private void mnuRunAllTests_Click(object sender, EventArgs e)
		{
			string workingDirectory = Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location));
			ProcessStartInfo startInfo = new ProcessStartInfo();
			startInfo.FileName = "TestHelper.exe";
			startInfo.WorkingDirectory = workingDirectory;
			Process.Start(startInfo);
		}
		
		private void mnuRunAllGameTests_Click(object sender, EventArgs e)
		{
			string workingDirectory = Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location));
			ProcessStartInfo startInfo = new ProcessStartInfo();
			startInfo.FileName = "TestHelper.exe";
			startInfo.Arguments = "\"" + Path.Combine(ConfigManager.HomeFolder, "TestGames") + "\"";
			startInfo.WorkingDirectory = workingDirectory;
			Process.Start(startInfo);
		}

		private void UpdateScaleMenu(double scale)
		{
			mnuScale1x.Checked = (scale == 1.0) && !_customSize;
			mnuScale2x.Checked = (scale == 2.0) && !_customSize;
			mnuScale3x.Checked = (scale == 3.0) && !_customSize;
			mnuScale4x.Checked = (scale == 4.0) && !_customSize;
			mnuScale5x.Checked = (scale == 5.0) && !_customSize;
			mnuScale6x.Checked = (scale == 6.0) && !_customSize;
			mnuScaleCustom.Checked = _customSize || !mnuScale1x.Checked && !mnuScale2x.Checked && !mnuScale3x.Checked && !mnuScale4x.Checked && !mnuScale5x.Checked && !mnuScale6x.Checked;

			ConfigManager.Config.VideoInfo.VideoScale = scale;
			ConfigManager.ApplyChanges();
		}

		private void UpdateFilterMenu(VideoFilterType filterType)
		{
			mnuNoneFilter.Checked = (filterType == VideoFilterType.None);
			mnuNtscFilter.Checked = (filterType == VideoFilterType.NTSC);
			mnuNtscBisqwitFullFilter.Checked = (filterType == VideoFilterType.BisqwitNtsc);
			mnuNtscBisqwitHalfFilter.Checked = (filterType == VideoFilterType.BisqwitNtscHalfRes);
			mnuNtscBisqwitQuarterFilter.Checked = (filterType == VideoFilterType.BisqwitNtscQuarterRes);

			mnuXBRZ2xFilter.Checked = (filterType == VideoFilterType.xBRZ2x);
			mnuXBRZ3xFilter.Checked = (filterType == VideoFilterType.xBRZ3x);
			mnuXBRZ4xFilter.Checked = (filterType == VideoFilterType.xBRZ4x);
			mnuXBRZ5xFilter.Checked = (filterType == VideoFilterType.xBRZ5x);
			mnuXBRZ6xFilter.Checked = (filterType == VideoFilterType.xBRZ6x);
			mnuHQ2xFilter.Checked = (filterType == VideoFilterType.HQ2x);
			mnuHQ3xFilter.Checked = (filterType == VideoFilterType.HQ3x);
			mnuHQ4xFilter.Checked = (filterType == VideoFilterType.HQ4x);
			mnuScale2xFilter.Checked = (filterType == VideoFilterType.Scale2x);
			mnuScale3xFilter.Checked = (filterType == VideoFilterType.Scale3x);
			mnuScale4xFilter.Checked = (filterType == VideoFilterType.Scale4x);
			mnu2xSaiFilter.Checked = (filterType == VideoFilterType._2xSai);
			mnuSuper2xSaiFilter.Checked = (filterType == VideoFilterType.Super2xSai);
			mnuSuperEagleFilter.Checked = (filterType == VideoFilterType.SuperEagle);
			mnuPrescale2xFilter.Checked = (filterType == VideoFilterType.Prescale2x);
			mnuPrescale3xFilter.Checked = (filterType == VideoFilterType.Prescale3x);
			mnuPrescale4xFilter.Checked = (filterType == VideoFilterType.Prescale4x);
			mnuPrescale6xFilter.Checked = (filterType == VideoFilterType.Prescale6x);
			mnuPrescale8xFilter.Checked = (filterType == VideoFilterType.Prescale8x);
			mnuPrescale10xFilter.Checked = (filterType == VideoFilterType.Prescale10x);

			ConfigManager.Config.VideoInfo.VideoFilter = filterType;
			ConfigManager.ApplyChanges();
		}

		private void mnuScale_Click(object sender, EventArgs e)
		{
			UInt32 scale = UInt32.Parse((string)((ToolStripMenuItem)sender).Tag);
			SetScale(scale);
		}

		private void SetScale(double scale)
		{
			_customSize = false;
			_regularScale = scale;
			if(this.HideMenuStrip) {
				this.menuStrip.Visible = false;
			}
			InteropEmu.SetVideoScale(scale);
			UpdateScaleMenu(scale);
			UpdateViewerSize();
		}

		private void SetVideoFilter(VideoFilterType type)
		{
			if(!_fullscreenMode) {
				_customSize = false;
			}
			InteropEmu.SetVideoFilter(type);
			UpdateFilterMenu(type);
		}

		private void mnuNoneFilter_Click(object sender, EventArgs e)
		{
			SetVideoFilter(VideoFilterType.None);
		}

		private void mnuNtscFilter_Click(object sender, EventArgs e)
		{
			SetVideoFilter(VideoFilterType.NTSC);
		}

		private void mnuXBRZ2xFilter_Click(object sender, EventArgs e)
		{
			SetVideoFilter(VideoFilterType.xBRZ2x);
		}
		
		private void mnuXBRZ3xFilter_Click(object sender, EventArgs e)
		{
			SetVideoFilter(VideoFilterType.xBRZ3x);
		}

		private void mnuXBRZ4xFilter_Click(object sender, EventArgs e)
		{
			SetVideoFilter(VideoFilterType.xBRZ4x);
		}

		private void mnuXBRZ5xFilter_Click(object sender, EventArgs e)
		{
			SetVideoFilter(VideoFilterType.xBRZ5x);
		}

		private void mnuXBRZ6xFilter_Click(object sender, EventArgs e)
		{
			SetVideoFilter(VideoFilterType.xBRZ6x);
		}

		private void mnuHQ2xFilter_Click(object sender, EventArgs e)
		{
			SetVideoFilter(VideoFilterType.HQ2x);
		}

		private void mnuHQ3xFilter_Click(object sender, EventArgs e)
		{
			SetVideoFilter(VideoFilterType.HQ3x);
		}

		private void mnuHQ4xFilter_Click(object sender, EventArgs e)
		{
			SetVideoFilter(VideoFilterType.HQ4x);
		}

		private void mnuScale2xFilter_Click(object sender, EventArgs e)
		{
			SetVideoFilter(VideoFilterType.Scale2x);
		}

		private void mnuScale3xFilter_Click(object sender, EventArgs e)
		{
			SetVideoFilter(VideoFilterType.Scale3x);
		}

		private void mnuScale4xFilter_Click(object sender, EventArgs e)
		{
			SetVideoFilter(VideoFilterType.Scale4x);
		}

		private void mnu2xSaiFilter_Click(object sender, EventArgs e)
		{
			SetVideoFilter(VideoFilterType._2xSai);
		}

		private void mnuSuper2xSaiFilter_Click(object sender, EventArgs e)
		{
			SetVideoFilter(VideoFilterType.Super2xSai);
		}

		private void mnuSuperEagleFilter_Click(object sender, EventArgs e)
		{
			SetVideoFilter(VideoFilterType.SuperEagle);
		}
		
		private void mnuPrescale2xFilter_Click(object sender, EventArgs e)
		{
			SetVideoFilter(VideoFilterType.Prescale2x);
		}

		private void mnuPrescale3xFilter_Click(object sender, EventArgs e)
		{
			SetVideoFilter(VideoFilterType.Prescale3x);
		}

		private void mnuPrescale4xFilter_Click(object sender, EventArgs e)
		{
			SetVideoFilter(VideoFilterType.Prescale4x);
		}

		private void mnuPrescale6xFilter_Click(object sender, EventArgs e)
		{
			SetVideoFilter(VideoFilterType.Prescale6x);
		}

		private void mnuPrescale8xFilter_Click(object sender, EventArgs e)
		{
			SetVideoFilter(VideoFilterType.Prescale8x);
		}

		private void mnuPrescale10xFilter_Click(object sender, EventArgs e)
		{
			SetVideoFilter(VideoFilterType.Prescale10x);
		}

		private void mnuNtscBisqwitFullFilter_Click(object sender, EventArgs e)
		{
			SetVideoFilter(VideoFilterType.BisqwitNtsc);
		}

		private void mnuNtscBisqwitHalfFilter_Click(object sender, EventArgs e)
		{
			SetVideoFilter(VideoFilterType.BisqwitNtscHalfRes);
		}

		private void mnuNtscBisqwitQuarterFilter_Click(object sender, EventArgs e)
		{
			SetVideoFilter(VideoFilterType.BisqwitNtscQuarterRes);
		}

		private void InitializeFdsDiskMenu()
		{
			if(this.InvokeRequired) {
				this.BeginInvoke((MethodInvoker)(() => this.InitializeFdsDiskMenu()));
			} else {
				UInt32 sideCount = InteropEmu.FdsGetSideCount();

				mnuSelectDisk.DropDownItems.Clear();

				if(sideCount > 0) {
					for(UInt32 i = 0; i < sideCount; i++) {
						UInt32 diskNumber = i;
						ToolStripItem item = mnuSelectDisk.DropDownItems.Add(ResourceHelper.GetMessage("FdsDiskSide", (diskNumber/2+1).ToString(), (diskNumber % 2 == 0 ? "A" : "B")));
						item.Click += (object sender, EventArgs args) => {
							InteropEmu.FdsInsertDisk(diskNumber);
						};
					}
					sepFdsDisk.Visible = true;
					mnuSelectDisk.Visible = true;
					mnuEjectDisk.Visible = true;
					mnuSwitchDiskSide.Visible = sideCount > 1;
				} else {
					sepFdsDisk.Visible = false;
					mnuSelectDisk.Visible = false;
					mnuEjectDisk.Visible = false;
					mnuSwitchDiskSide.Visible = false;
				}
			}
		}

		private void mnuEjectDisk_Click(object sender, EventArgs e)
		{
			InteropEmu.FdsEjectDisk();
		}

		private void mnuSwitchDiskSide_Click(object sender, EventArgs e)
		{
			InteropEmu.FdsSwitchDiskSide();
		}

		private void SelectFdsBiosPrompt()
		{
			if(MesenMsgBox.Show("FdsBiosNotFound", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK) {
				OpenFileDialog ofd = new OpenFileDialog();
				ofd.SetFilter(ResourceHelper.GetMessage("FilterAll"));
				if(ofd.ShowDialog() == DialogResult.OK) {
					string hash = MD5Helper.GetMD5Hash(ofd.FileName).ToLowerInvariant();
					if(hash == "ca30b50f880eb660a320674ed365ef7a" || hash == "c1a9e9415a6adde3c8563c622d4c9fce") {
						File.Copy(ofd.FileName, Path.Combine(ConfigManager.HomeFolder, "FdsBios.bin"));
						LoadROM(_currentRomPath, ConfigManager.Config.PreferenceInfo.AutoLoadIpsPatches);
					} else {
						MesenMsgBox.Show("InvalidFdsBios", MessageBoxButtons.OK, MessageBoxIcon.Error);
					}
				}
			}
		}

		private void frmMain_DragDrop(object sender, DragEventArgs e)
		{
			string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
			if(File.Exists(files[0])) {
				LoadFile(files[0]);
				this.Activate();
			}
		}

		private void frmMain_DragEnter(object sender, DragEventArgs e)
		{
			if(e.Data.GetDataPresent(DataFormats.FileDrop)) {
				e.Effect = DragDropEffects.Copy;
			}
		}

		private void mnuNetPlayPlayer1_Click(object sender, EventArgs e)
		{
			InteropEmu.NetPlaySelectController(0);
		}

		private void mnuNetPlayPlayer2_Click(object sender, EventArgs e)
		{
			InteropEmu.NetPlaySelectController(1);
		}

		private void mnuNetPlayPlayer3_Click(object sender, EventArgs e)
		{
			InteropEmu.NetPlaySelectController(2);
		}

		private void mnuNetPlayPlayer4_Click(object sender, EventArgs e)
		{
			InteropEmu.NetPlaySelectController(3);
		}

		private void mnuNetPlaySpectator_Click(object sender, EventArgs e)
		{
			InteropEmu.NetPlaySelectController(0xFF);
		}

		private void mnuFullscreen_Click(object sender, EventArgs e)
		{
			SetFullscreenState(!_fullscreenMode);
		}

		private void ctrlRenderer_DoubleClick(object sender, EventArgs e)
		{
			if(!ctrlRenderer.NeedMouseIcon && !InteropEmu.HasArkanoidPaddle()) {
				//Disable double clicking (used to switch to fullscreen mode) when using zapper/arkanoid controller
				SetFullscreenState(!_fullscreenMode);
			}
		}

		private void mnuScaleCustom_Click(object sender, EventArgs e)
		{
			SetScaleBasedOnWindowSize();
		}

		private void panelRenderer_Click(object sender, EventArgs e)
		{
			if(this.HideMenuStrip) {
				this.menuStrip.Visible = false;
			}

			ctrlRenderer.Focus();
		}

		private void ctrlRenderer_Enter(object sender, EventArgs e)
		{
			if(this.HideMenuStrip) {
				this.menuStrip.Visible = false;
			}
		}

		private void menuStrip_VisibleChanged(object sender, EventArgs e)
		{
			if(this.HideMenuStrip) {
				IntPtr handle = this.Handle;
				this.BeginInvoke((MethodInvoker)(() => {
					int rendererTop = (panelRenderer.Height + (this.menuStrip.Visible ? menuStrip.Height : 0) - ctrlRenderer.Height) / 2;
					this.ctrlRenderer.Top = rendererTop + (this.menuStrip.Visible ? -menuStrip.Height : 0);
				}));
			}
		}

		private void mnuAbout_Click(object sender, EventArgs e)
		{
			new frmAbout().ShowDialog();
		}

		private void CheckForUpdates(bool displayResult)
		{
			Task.Run(() => {
				try {
					using(var client = new WebClient()) {
						XmlDocument xmlDoc = new XmlDocument();

						string platform = Program.IsMono ? "linux" : "win";
						xmlDoc.LoadXml(client.DownloadString("http://www.mesen.ca/Services/GetLatestVersion.php?v=" + InteropEmu.GetMesenVersion() + "&p=" + platform + "&l=" + ResourceHelper.GetLanguageCode()));
						Version currentVersion = new Version(InteropEmu.GetMesenVersion());
						Version latestVersion = new Version(xmlDoc.SelectSingleNode("VersionInfo/LatestVersion").InnerText);
						string changeLog = xmlDoc.SelectSingleNode("VersionInfo/ChangeLog").InnerText;
						string fileHash = xmlDoc.SelectSingleNode("VersionInfo/Sha1Hash").InnerText;
						string donateText = xmlDoc.SelectSingleNode("VersionInfo/DonateText")?.InnerText;

						if(latestVersion > currentVersion) {
							this.BeginInvoke((MethodInvoker)(() => {
								frmUpdatePrompt frmUpdate = new frmUpdatePrompt(currentVersion, latestVersion, changeLog, fileHash, donateText);
								if(frmUpdate.ShowDialog(null, this) == DialogResult.OK) {
									Application.Exit();
								}
							}));
						} else if(displayResult) {
							MesenMsgBox.Show("MesenUpToDate", MessageBoxButtons.OK, MessageBoxIcon.Information);
						}
					}
				} catch(Exception ex) {
					if(displayResult) {
						MesenMsgBox.Show("ErrorWhileCheckingUpdates", MessageBoxButtons.OK, MessageBoxIcon.Error, ex.ToString());
					}
				}
			});
		}

		private void mnuCheckForUpdates_Click(object sender, EventArgs e)
		{
			CheckForUpdates(true);
		}
		
		private void mnuReportBug_Click(object sender, EventArgs e)
		{
			Process.Start("http://www.mesen.ca/ReportBug.php");
		}

		private void InitializeVsSystemMenu()
		{
			if(this.InvokeRequired) {
				this.BeginInvoke((MethodInvoker)(() => InitializeVsSystemMenu()));
			} else {
				sepVsSystem.Visible = InteropEmu.IsVsSystem();
				mnuInsertCoin1.Visible = InteropEmu.IsVsSystem();
				mnuInsertCoin2.Visible = InteropEmu.IsVsSystem();
				mnuVsGameConfig.Visible = InteropEmu.IsVsSystem();
			}
		}

		private void mnuInsertCoin1_Click(object sender, EventArgs e)
		{
			InteropEmu.VsInsertCoin(0);
		}

		private void mnuInsertCoin2_Click(object sender, EventArgs e)
		{
			InteropEmu.VsInsertCoin(1);
		}

		private void ShowVsGameConfig()
		{
			VsConfigInfo configInfo = VsConfigInfo.GetCurrentGameConfig(true);
			if(new frmVsGameConfig(configInfo).ShowDialog(null, this) == DialogResult.OK) {
				VsConfigInfo.ApplyConfig();
			}
		}

		private void mnuVsGameConfig_Click(object sender, EventArgs e)
		{
			ShowVsGameConfig();
		}

		private void mnuBilinearInterpolation_Click(object sender, EventArgs e)
		{
			ConfigManager.Config.VideoInfo.UseBilinearInterpolation = mnuBilinearInterpolation.Checked;
			ConfigManager.ApplyChanges();
			VideoInfo.ApplyConfig();
		}

		private void mnuLogWindow_Click(object sender, EventArgs e)
		{
			if(_logWindow == null) {
				_logWindow = new frmLogWindow();
				_logWindow.StartPosition = FormStartPosition.Manual;
				_logWindow.Left = this.Left + (this.Width - _logWindow.Width) / 2;
				_logWindow.Top = this.Top + (this.Height - _logWindow.Height) / 2;
				_logWindow.Show(sender, null);
				_logWindow.FormClosed += (object a, FormClosedEventArgs b) => {
					_logWindow = null;
				};
			} else {
				_logWindow.Focus();
			}
		}

		private void mnuEmulationConfig_Click(object sender, EventArgs e)
		{
			new frmEmulationConfig().ShowDialog(sender);
		}

		private void InitializeNsfMode(bool updateTextOnly = false, bool gameLoaded = false)
		{
			if(this.InvokeRequired) {
				if(InteropEmu.IsNsf()) {
					if(InteropEmu.IsConnected()) {
						InteropEmu.Disconnect();
					}
					if(InteropEmu.IsServerRunning()) {
						InteropEmu.StopServer();
					}
				}
				this.BeginInvoke((MethodInvoker)(() => this.InitializeNsfMode(updateTextOnly, gameLoaded)));
			} else {
				if(InteropEmu.IsNsf()) {
					if(gameLoaded) {
						//Force emulation speed to 100 when loading a NSF
						SetEmulationSpeed(100);
					}

					if(!this._isNsfPlayerMode) {
						this.Size = new Size(380, 320);
						this.MinimumSize = new Size(380, 320);
					}
					this._isNsfPlayerMode = true;
					this.ctrlNsfPlayer.UpdateText();
					if(!updateTextOnly) {
						this.ctrlNsfPlayer.ResetCount();
					}
					this.ctrlNsfPlayer.Visible = true;
					this.ctrlNsfPlayer.Focus();
					
					_currentGame = InteropEmu.NsfGetHeader().GetSongName();
				} else if(this._isNsfPlayerMode) {
					this.MinimumSize = new Size(335, 320);
					this.SetScale(_regularScale);
					this._isNsfPlayerMode = false;
					this.ctrlNsfPlayer.Visible = false;
				}
			}
		}

		private void mnuRandomGame_Click(object sender, EventArgs e)
		{
			IEnumerable<string> gameFolders = ConfigManager.Config.RecentFiles.Select(recentFile => Path.GetDirectoryName(recentFile.Path).ToLowerInvariant()).Distinct();
			List<string> gameRoms = new List<string>();

			foreach(string folder in gameFolders) {
				if(Directory.Exists(folder)) {
					gameRoms.AddRange(Directory.EnumerateFiles(folder, "*.nes", SearchOption.TopDirectoryOnly));
					gameRoms.AddRange(Directory.EnumerateFiles(folder, "*.unf", SearchOption.TopDirectoryOnly));
					gameRoms.AddRange(Directory.EnumerateFiles(folder, "*.fds", SearchOption.TopDirectoryOnly));
				}
			}

			if(gameRoms.Count == 0) {
				MesenMsgBox.Show("RandomGameNoGameFound", MessageBoxButtons.OK, MessageBoxIcon.Information);
			} else {
				Random random = new Random();
				string randomGame = gameRoms[random.Next(gameRoms.Count - 1)];
				LoadFile(randomGame);
			}
		}

		private void mnuHelpWindow_Click(object sender, EventArgs e)
		{
			using(frmHelp frm = new frmHelp()) {
				frm.ShowDialog(sender, this);
			}
		}
	}
}
