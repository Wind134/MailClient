using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MailClient.Setup.Bootstrap
{
    /// <summary>
    /// 单文件安装包的入口：解压嵌入的 payload.zip 到临时目录 → 启动里面的 WinUI 安装界面 → 等它结束 → 清理临时目录。
    /// 命令行参数原样传给安装界面（/S 静默、/D=路径、/uninstall 等）。
    /// </summary>
    internal static class Program
    {
        private const string SetupExe = "MailClient.Setup.exe";

        [STAThread]
        private static int Main(string[] args)
        {
            var silent = false;
            foreach (var a in args)
            {
                var t = a.TrimStart('/', '-').ToLowerInvariant();
                if (t == "s" || t == "silent" || t == "quiet")
                {
                    silent = true;
                }
            }

            var work = Path.Combine(Path.GetTempPath(), "MailClient-Setup-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var exitCode = 0;

            try
            {
                if (silent)
                {
                    Extract(work, null);
                    exitCode = RunSetup(work, args, null);
                }
                else
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    using (var splash = new SplashForm())
                    {
                        string error = null;
                        Task worker = null;
                        splash.Shown += (s, e) => worker = Task.Run(() =>
                        {
                            try
                            {
                                Extract(work, splash.SetProgress);
                                exitCode = RunSetup(work, args, splash);
                            }
                            catch (Exception ex)
                            {
                                error = ex.Message;
                                exitCode = 1;
                            }
                            finally
                            {
                                splash.CloseFromWorker();
                            }
                        });
                        Application.Run(splash);
                        if (worker != null)
                        {
                            worker.Wait(); // 启动画面收起后，安装界面还在跑：必须等它结束再清理临时目录
                        }

                        if (error != null)
                        {
                            MessageBox.Show("无法启动安装程序：" + error, "邮件 安装程序", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!silent)
                {
                    MessageBox.Show("无法启动安装程序：" + ex.Message, "邮件 安装程序", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }

                exitCode = 1;
            }
            finally
            {
                Cleanup(work);
            }

            return exitCode;
        }

        private static void Extract(string work, Action<double> progress)
        {
            Directory.CreateDirectory(work);
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException("安装包不完整（缺少内置文件），请重新下载。");
                }

                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    long total = 0;
                    foreach (var entry in zip.Entries)
                    {
                        total += entry.Length;
                    }

                    long done = 0;
                    var root = Path.GetFullPath(work);
                    foreach (var entry in zip.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name))
                        {
                            continue; // 目录项
                        }

                        var dest = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                        if (!dest.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException("安装包内容异常。");
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        using (var input = entry.Open())
                        using (var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
                        {
                            input.CopyTo(output);
                        }

                        done += entry.Length;
                        if (progress != null && total > 0)
                        {
                            progress((double)done / total);
                        }
                    }
                }
            }
        }

        private static int RunSetup(string work, string[] args, SplashForm splash)
        {
            var exe = Path.Combine(work, SetupExe);
            if (!File.Exists(exe))
            {
                throw new FileNotFoundException("安装包不完整（找不到安装界面）。");
            }

            var info = new ProcessStartInfo(exe, JoinArgs(args))
            {
                WorkingDirectory = work,
                UseShellExecute = false,
            };
            using (var process = Process.Start(info))
            {
                if (process == null)
                {
                    throw new InvalidOperationException("无法启动安装界面。");
                }

                if (splash != null)
                {
                    // 等安装窗口出现后再收起启动画面，避免中间出现空档
                    var deadline = DateTime.UtcNow.AddSeconds(30);
                    while (!process.HasExited && DateTime.UtcNow < deadline)
                    {
                        process.Refresh();
                        if (process.MainWindowHandle != IntPtr.Zero)
                        {
                            Thread.Sleep(250);
                            break;
                        }

                        Thread.Sleep(100);
                    }

                    splash.CloseFromWorker();
                }

                process.WaitForExit();
                return process.ExitCode;
            }
        }

        private static string JoinArgs(string[] args)
        {
            var parts = new string[args.Length];
            for (var i = 0; i < args.Length; i++)
            {
                parts[i] = args[i].IndexOf(' ') >= 0 && !args[i].StartsWith("\"", StringComparison.Ordinal) ? "\"" + args[i] + "\"" : args[i];
            }

            return string.Join(" ", parts);
        }

        private static void Cleanup(string work)
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    if (Directory.Exists(work))
                    {
                        Directory.Delete(work, true);
                    }

                    return;
                }
                catch (Exception)
                {
                    Thread.Sleep(500);
                }
            }
        }
    }
}
