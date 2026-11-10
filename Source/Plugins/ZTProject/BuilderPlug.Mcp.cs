#region ================== Namespaces

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using CodeImp.DoomBuilder.Config;
using CodeImp.DoomBuilder.IO;
using CodeImp.DoomBuilder.Map;
using CodeImp.DoomBuilder.Windows;
using CodeImp.DoomBuilder.ZT;
using CodeImp.DoomBuilder.ZTProject.Mcp;

#endregion

namespace CodeImp.DoomBuilder.ZTProject
{
	// MCP-сервер UDB в плагине ZT (ZT/mcp-plan.md): меню режимов, файл подключения для агентов на этом ПК,
	// счётчик изменений карты и операции плагина для инструментов zt_*.
	public partial class BuilderPlug
	{
		#region ================== Variables

		private const int MCP_PORT = 47390;

		private McpServer mcp;
		private McpTools mcptools;
		private ToolStripMenuItem mcpmenu;
		private ToolStripMenuItem[] mcpmodes;
		private ToolStripStatusLabel mcplabel;

		private static string McpFile { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZTWorkbench", "mcp.json"); } }

		#endregion

		#region ================== Init

		private void InitMcp()
		{
			mcptools = new McpTools(this);

			mcpmenu = new ToolStripMenuItem("MCP-сервер");
			string[] titles = { "Выключен", "Только чтение", "Чтение и правка" };
			mcpmodes = new ToolStripMenuItem[titles.Length];
			for(int i = 0; i < titles.Length; i++)
			{
				McpMode m = (McpMode)i;
				mcpmodes[i] = new ToolStripMenuItem(titles[i], null, (s, e) => SetMcpMode(m, true));
				mcpmenu.DropDownItems.Add(mcpmodes[i]);
			}
			mcpmenu.DropDownItems.Add(new ToolStripSeparator());
			mcpmenu.DropDownItems.Add(new ToolStripMenuItem("Скопировать подключение для Claude Code", null, (s, e) => CopyMcpConnection()));
			menu.DropDownItems.Add(new ToolStripSeparator());
			menu.DropDownItems.Add(mcpmenu);

			mcplabel = new ToolStripStatusLabel { Visible = false, Margin = new Padding(4, 3, 4, 2) };
			General.MainWindow.AddStatusItem(mcplabel);

			// Режим — из настроек; переменная ZT_MCP (off / ro / rw) его заменяет: так агент запускает UDB с сервером
			McpMode mode = (McpMode)General.Clamp(General.Settings.ReadPluginSetting("mcpmode", 0), 0, 2);
			string env = Environment.GetEnvironmentVariable("ZT_MCP");
			if(!string.IsNullOrEmpty(env))
			{
				env = env.Trim().ToLowerInvariant();
				mode = env == "rw" || env == "readwrite" ? McpMode.ReadWrite : env == "ro" || env == "readonly" ? McpMode.ReadOnly : McpMode.Off;
			}
			SetMcpMode(mode, false);
		}

		private void DisposeMcp()
		{
			StopMcp();
			if(mcplabel != null) General.MainWindow.RemoveStatusItem(mcplabel);
		}

		#endregion

		#region ================== Server

		private static string McpToken()
		{
			string token = General.Settings.ReadPluginSetting("mcptoken", "");
			if(token.Length >= 32) return token;
			byte[] b = new byte[16];
			using(RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
			token = BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
			General.Settings.WritePluginSetting("mcptoken", token);
			General.SaveSettings();     // токен уже отдан агентам — не потерять при сбое UDB
			return token;
		}

		private void SetMcpMode(McpMode mode, bool save)
		{
			if(save) { General.Settings.WritePluginSetting("mcpmode", (int)mode); General.SaveSettings(); }
			if(mode == McpMode.Off) StopMcp();
			else if(mcp != null) { mcp.Mode = mode; WriteMcpFile(); }
			else
			{
				string error = null;
				for(int port = MCP_PORT; port < MCP_PORT + 10 && mcp == null; port++)
				{
					McpServer s = new McpServer(mcptools, port, McpToken(), mode);
					try { s.Start(); mcp = s; }
					catch(Exception ex) { error = ex.Message; }
				}
				if(mcp == null)
				{
					General.ShowErrorMessage("ZT: MCP-сервер не запустился.\n" + error, MessageBoxButtons.OK);
					mode = McpMode.Off;
				}
				else
				{
					mcp.Called += text => General.MainWindow.BeginInvoke((MethodInvoker)(() => { if(mcplabel != null) mcplabel.ToolTipText = "ZT MCP: " + mcp.Url + "\nпоследний вызов: " + text; }));
					WriteMcpFile();
					General.WriteLogLine("ZT: MCP-сервер " + mcp.Url + " (" + mode + ")");
				}
			}
			for(int i = 0; i < mcpmodes.Length; i++) mcpmodes[i].Checked = (int)mode == i;
			mcplabel.Text = mode == McpMode.ReadWrite ? "MCP: правка" : "MCP: чтение";
			mcplabel.Visible = mode != McpMode.Off;
			if(mcp != null) mcplabel.ToolTipText = "ZT MCP: " + mcp.Url;
		}

		private void StopMcp()
		{
			if(mcp == null) return;
			mcp.Stop();
			mcp = null;
			try { File.Delete(McpFile); } catch(Exception) { }
		}

		// Файл подключения для агентов на этом ПК: адрес, токен, режим
		private void WriteMcpFile()
		{
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(McpFile));
				string json = new JavaScriptSerializer().Serialize(new Dictionary<string, object>
				{
					{ "url", mcp.Url }, { "token", mcp.Token }, { "mode", mcp.Mode.ToString() },
					{ "pid", System.Diagnostics.Process.GetCurrentProcess().Id }
				});
				File.WriteAllText(McpFile, json, new UTF8Encoding(false));
			}
			catch(Exception ex)
			{
				General.WriteLogLine("ZT: не записан " + McpFile + ": " + ex.Message);
			}
		}

		private void CopyMcpConnection()
		{
			if(mcp == null)
			{
				General.ShowWarningMessage("ZT: MCP-сервер выключен (ZT → MCP-сервер).", MessageBoxButtons.OK);
				return;
			}
			string cmd = "claude mcp add --transport http udb " + mcp.Url + " --header \"Authorization: Bearer " + mcp.Token + "\"";
			Clipboard.SetText(cmd);
			General.Interface.DisplayStatus(StatusType.Info, "ZT: команда подключения скопирована — выполните её в терминале.");
		}

		#endregion

		#region ================== Map revision

		public override void OnUndoCreated() { base.OnUndoCreated(); McpTools.Revision++; }
		public override void OnUndoEnd() { base.OnUndoEnd(); McpTools.Revision++; }
		public override void OnRedoEnd() { base.OnRedoEnd(); McpTools.Revision++; }
		public override void OnUndoWithdrawn() { base.OnUndoWithdrawn(); McpTools.Revision++; }

		#endregion

		#region ================== Operations for MCP tools

		internal Dictionary<string, object> McpStatus()
		{
			string dir = ProjectDir;
			bool valid = ProjectMap.IsProjectDir(dir);
			return new Dictionary<string, object>
			{
				{ "project_dir", dir }, { "project_valid", valid }, { "branch", valid ? ReadBranch(dir) : null },
				{ "project_map", current != null ? current.MapName : null }
			};
		}

		internal string McpSetProject(string dir)
		{
			if(!ProjectMap.IsProjectDir(dir)) throw new ArgumentException("в папке нет " + ProjectMap.MANIFEST);
			ProjectDir = Path.GetFullPath(dir);
			UpdateBranchLabel();
			return "папка проекта: " + ProjectDir + ", ветка " + ReadBranch(ProjectDir);
		}

		// Открыть карту проекта без окна параметров: параметры — из .dbs временного WAD или той же карты другой рабочей копии
		internal string McpOpenMap(string mapname)
		{
			string dir = ProjectDir;
			if(!ProjectMap.IsProjectDir(dir)) throw new InvalidOperationException("папка проекта не выбрана (zt_set_project)");
			mapname = (mapname ?? "").ToUpperInvariant();
			if(!ProjectMap.ListMaps(dir).Contains(mapname)) throw new ArgumentException("в проекте нет карты " + mapname);
			if(General.Map != null && General.Map.IsChanged)
				throw new InvalidOperationException("в открытой карте есть несохранённые правки — их сохраняет или отменяет человек");

			ProjectMap pm = ProjectMap.Load(dir, mapname);
			string dbs = Path.ChangeExtension(pm.WadPath, ".dbs");
			if(!File.Exists(dbs))
			{
				string root = Path.GetDirectoryName(Path.GetDirectoryName(pm.WadPath));
				string src = Directory.GetFiles(root, mapname + ".dbs", SearchOption.AllDirectories).FirstOrDefault() ??
				             Directory.GetFiles(root, "*.dbs", SearchOption.AllDirectories).FirstOrDefault();
				if(src == null) throw new InvalidOperationException("нет параметров карты (.dbs) — один раз откройте карту проекта в UDB вручную");
				File.Copy(src, dbs);
			}
			Configuration cfg = new Configuration(dbs, true);
			string configfile = cfg.ReadSetting("gameconfig", "");
			if(!General.ConfigurationInfoExist(configfile)) throw new InvalidOperationException("в " + dbs + " нет известной игровой конфигурации");
			ConfigurationInfo ci = General.GetConfigurationInfo(configfile);
			MapOptions options = new MapOptions(cfg, mapname, ci.Configuration.ReadSetting("longtexturenames", false));
			options.ConfigFile = configfile;

			pm.BuildWad();
			projectmaps[pm.WadPath] = pm;
			General.Settings.WritePluginSetting("lastmap", mapname);
			General.OpenMapFileWithOptions(pm.WadPath, options);
			if(General.Map == null) throw new InvalidOperationException("UDB не открыл карту (см. UDBuilder.log)");
			return "открыта " + mapname + " из " + pm.ProjectDir + " (конфигурация " + ci.Name + ")";
		}

		internal string McpReloadMap()
		{
			if(current == null || General.Map == null) throw new InvalidOperationException("открыта не карта проекта");
			try { return ReloadCurrentMap("ZT: карта перечитана из файлов (MCP)", true); }
			finally { Cursor.Current = Cursors.Default; }
		}

		#endregion
	}
}
