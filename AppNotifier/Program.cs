using AppNotifier.Properties;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ZeroMQ;

namespace AppNotifier
{
    public struct Alert
    {
        public string Message;
        public SoundPlayer SoundAlert;

        public Alert(string message, string soundFile)
        {
            Message = message;
            if (soundFile != "none")
            {
                SoundAlert = new SoundPlayer(soundFile);
                SoundAlert.Load();
            }
            else
            {
                SoundAlert = null;
            }
        }
    }

    static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static int Main(string[] args)
        {
            bool server = false, doLog = false;
            string address = "", alert = "";
            Dictionary<string, Alert> alerts = new Dictionary<string, Alert>();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--server")
                {
                    server = !server;
                }
                else if (args[i] == "--log")
                {
                    doLog = !doLog;
                }
                else if (args[i] == "--address")
                {
                    if (i == args.Length - 1)
                    {
                        MessageBox.Show("No address specified.", null,
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return 1;
                    }
                    address = args[++i];
                }
                else if (args[i] == "--add")
                {
                    if (i >= args.Length - 3)
                    {
                        MessageBox.Show("Not enough parameters specified for an alert.", null,
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return 1;
                    }
                    else
                    {
                        try
                        {
                            Alert a = new Alert(args[i + 2], args[i + 3]);
                            alerts.Add(args[i + 1], a);
                        } catch (FileNotFoundException e)
                        {
                            MessageBox.Show(String.Format("Invalid sound specified for '{0}': {1}: {2}",
                                args[i + 1], args[i + 3], e.Message), null,
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return 1;
                        }
                        i += 3;
                    }
                }
                else
                {
                    alert = args[i];
                }
            }

            if (doLog)
            {
                TextWriterTraceListener listener = new TextWriterTraceListener("AppNotifierLog.log")
                {
                    TraceOutputOptions = TraceOptions.DateTime | TraceOptions.ProcessId
                };
                Trace.Listeners.Add(listener);
                Trace.AutoFlush = true;
            }

            if (server)
            {
                Application.Run(new ServerApplicationContext(address, alerts));
                return Environment.ExitCode;
            }
            else
            {
                if (alert.Length == 0)
                {
                    MessageBox.Show("No alert specified.", null,
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return 1;
                }
                else if (address.Length == 0)
                {
                    MessageBox.Show("No address specified.",null,
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return 1;
                }
                else
                {
                    try
                    {
                        return Client(address, alert);
                    }
                    catch (Exception e)
                    {
                        Trace.TraceWarning("Failed to send alert: {0}", e.Message);
                        return 1;
                    }
                }
            }
        }

        static int Client(string address, string request)
        {
            // Create
            using (var context = new ZContext())
            using (var requester = new ZSocket(context, ZSocketType.REQ))
            {
                // Connect
                requester.SetOption(ZSocketOption.LINGER, 0);
                requester.Connect(address);

                // Send
                Trace.TraceInformation("Sending request {0}... ", request);
                requester.Send(new ZFrame(request));

                // Receive
                ZPollItem poll = ZPollItem.CreateReceiver();
                ZError error;
                ZMessage reply;
                if (requester.PollIn(poll, out reply, out error, TimeSpan.FromSeconds(2)))
                {
                    using (reply)
                    {
                        string response = reply.PopString();
                        Trace.TraceInformation("Response: {0}", response);
                    }
                }
                else
                {
                    Trace.TraceInformation("Failed to send alarm (timeout)");
                    return 1;
                }
            }
            return 0;
        }
    }

    public class ServerApplicationContext : ApplicationContext
    {
        private NotifyIcon trayIcon;
        private BackgroundWorker worker;
        private Dictionary<string, Alert> alerts;
        private string address;

        public ServerApplicationContext(string address, Dictionary<string, Alert> alerts)
        {
            this.alerts = alerts;
            this.address = address;

            if (this.address.Length == 0)
            {
                Trace.TraceInformation("No address specified - defaulting to port 5555.");
                this.address = "tcp://*:5555";
            }

            trayIcon = new NotifyIcon()
            {
                Icon = Resources.AppIcon,
                ContextMenu = new ContextMenu(new MenuItem[] {
                    new MenuItem("E&xit", Exit)
                }),
                Visible = true,
                Text = "App Notifier"
            };
            worker = new BackgroundWorker();
            worker.WorkerSupportsCancellation = true;
            worker.WorkerReportsProgress = true;
            worker.DoWork += Server;
            worker.ProgressChanged += worker_ProgressChanged;
            worker.RunWorkerCompleted += worker_RunWorkerCompleted;
            worker.RunWorkerAsync();
        }

        private void worker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            if (e.Error != null)
            {
                string emsg = String.Format("Error running server: {0}", e.Error.Message);
                MessageBox.Show(emsg,
                    "AppNotifier - Error", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                Trace.TraceWarning(emsg);
                Environment.ExitCode = 1;
            }
            Trace.TraceInformation("Server shutdown.");

            // Hide tray icon, otherwise it will remain shown until user mouses over it
            trayIcon.Visible = false;
            Application.Exit();
        }

        private void worker_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            string alert = e.UserState as string;
            if (alerts.ContainsKey(alert))
            {
                Alert a = alerts[alert];
                trayIcon.ShowBalloonTip(30000, "AppNotifier", a.Message, ToolTipIcon.Info);
                if (a.SoundAlert != null)
                {
                    try
                    {
                        a.SoundAlert.Play();
                    }
                    catch (InvalidOperationException err)
                    {
                        Trace.TraceError("Could not play sound for alert '{0}': {1}", alert, err.Message);
                    }
                }
                Trace.TraceInformation("Received alert {0} ({1})", alert, a.Message);
            }
            else
            {
                Trace.TraceWarning("Unknown alert {0} received.", alert);
            }
        }

        private void Server(object sender, DoWorkEventArgs e)
        {
            BackgroundWorker worker = sender as BackgroundWorker;

            // Create
            using (var context = new ZContext())
            using (var server = new ZSocket(context, ZSocketType.REP))
            {
                // Bind
                server.Bind(address);
                Trace.TraceInformation("Server started.");

                ZMessage message;
                ZError error;
                while (true)
                {
                    if (worker.CancellationPending)
                    {
                        e.Cancel = true;
                        break;
                    }
                    else if ((message = server.ReceiveMessage(ZSocketFlags.DontWait, out error)) != null)
                    {
                        using (message)
                        {
                            worker.ReportProgress(0, message.PopString());
                            message.Clear();
                            message.Add(new ZFrame("ACK"));
                            server.Send(message);
                        }
                    }
                    else
                    {
                        if (error == ZError.ETERM)
                            return; // Interrupted
                        else if (error == ZError.EAGAIN)
                        {
                            Thread.Sleep(100);
                            continue; //No message
                        }
                        throw new ZException(error);
                    }
                }
            }
        }

        private void Exit(object sender, EventArgs e)
        {
            worker.CancelAsync();
        }
    }
}
