namespace SSEtoRserve
{
    #region Usings
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Net;
    using System.Text;
    using System.Threading.Tasks;
    using RserveCLI2;
    using System.Threading;
    using System.IO;
    using NLog;
    using System.Collections.Concurrent;
    using System.Net.Sockets;
    using System.Reflection;
    using System.Net.NetworkInformation;
    #endregion

    public class RserveParameter
    {
        #region Properties & Variables
        private Uri ConnUri { get; set; }
        public string Hostname { get; private set; }
        public IPAddress IpAddress { get; private set; }
        public int Port { get; private set; }
        public string InitScript { get; private set; }
        public string RProcessCommandLineArgs { get; private set; }
        public string RTermPath { get; private set; }
        public bool UseIpAddress { get; private set; }
        public string Scheme { get; private set; }
        public string User { get; private set; }
        public string Password { get; private set; }
        public NetworkCredential Credentials { get; private set; }

        public bool RTermPathExits
        {
            get
            {
                return File.Exists(RTermPath);
            }
        }
        #endregion

        #region Constructor
        public RserveParameter(Uri uri, int rservePort, string initScript = null, string rProcessCommandLineArgs = null, string rserveUser = null, string rservePassword = null)
        {
            ConnUri = uri;
            Port = rservePort;
            InitScript = initScript;
            RProcessCommandLineArgs = rProcessCommandLineArgs;
            User = rserveUser;
            Password = rservePassword;
            Init();
        }
        
        private void Init()
        {
            Scheme = ConnUri.Scheme.ToUpperInvariant();
            switch (Scheme)
            {
                case "RSERVE":
                    Hostname = ConnUri.Host;
                    break;
                case "FILE":
                    Hostname = "127.0.0.1";
                    RTermPath = ConnUri.LocalPath;
                    break;

                default:
                    throw new Exception($"The uri scheme \"{ConnUri.Scheme}\" is unknown.");
            }

            IPAddress address = null;
            if (IPAddress.TryParse(Hostname, out address))
            {
                IpAddress = address;
                UseIpAddress = true;
            }

            Credentials = null;
            if (!String.IsNullOrEmpty(User))
            {
                Credentials = new NetworkCredential(User, Password);
            }
        }

        private int GetAvailablePort(int startPort)
        {
            var ipGlobalProperties = IPGlobalProperties.GetIPGlobalProperties();
            var tcpConnInfoArray = ipGlobalProperties.GetActiveTcpConnections();

            foreach (TcpConnectionInformation tcpi in tcpConnInfoArray)
            {
                if (tcpi.LocalEndPoint.Port == startPort)
                {
                    //Port busy, next port
                    startPort++;
                    return GetAvailablePort(startPort);
                }
            }

            return startPort;
        }

        #endregion
    }

    public class RserveConnectionPool : IDisposable
    {
        #region Proberties & Variables
        private ConcurrentDictionary<string, RserveConnection> rserveConns;
        private ConcurrentDictionary<string, DateTime> lastUsed;
        #endregion

        #region Constructor
        public RserveConnectionPool()
        {
            rserveConns = new ConcurrentDictionary<string, RserveConnection>();
            lastUsed = new ConcurrentDictionary<string, DateTime>();

            // TODO: Maybe add a cleanup of old connections that are not used
        }
        #endregion

        #region Methods
        public RserveConnection GetConnection(RserveParameter rserveParams)
        {
            var hash = rserveParams.GetHashCode().ToString();
            RserveConnection conn = null;

            if (rserveConns.ContainsKey(hash))
            {
                rserveConns.TryGetValue(hash, out conn);
                var dateTime = DateTime.MinValue;
                if (lastUsed.TryGetValue(hash, out dateTime))
                {
                    lastUsed.TryUpdate(hash, DateTime.Now, dateTime);
                }
            }
            else
            {
                conn = new RserveConnection(rserveParams);
                Thread.Sleep(500);
                rserveConns.TryAdd(hash, conn);
                lastUsed.TryAdd(hash, DateTime.Now);
            }
            
            return conn;
        }

        public void Dispose()
        {
            foreach (var conn in rserveConns.Values)
            {
                try
                {
                    conn.Dispose();
                }
                catch
                {
                }
            }
        }
        #endregion
    }

    public class RserveConnection: IDisposable
    {
        #region Logger
        private static Logger logger = LogManager.GetCurrentClassLogger();
        #endregion

        #region Properties & Variables
        public RConnection Connection { get; set; }
        private CancellationTokenSource RserveToken { get; set; }
        private CancellationTokenSource RTermToken { get; set; }
        private Process RtermProcess { get; set; }
        private StringBuilder ProcessLog { get; set; } = new StringBuilder();
        private RserveParameter Parameter { get; set; }
        #endregion

        #region Constructor & Dispose
        public RserveConnection(RserveParameter parameter)
        {
            Parameter = parameter;

            try
            {
                if (Parameter.Scheme == "FILE")
                {
                    RTermToken = new CancellationTokenSource();
                    Task.Factory.StartNew(FindRTermProcess, RTermToken.Token,
                                          TaskCreationOptions.LongRunning, TaskScheduler.Default);
                }

                switch (Parameter.Scheme)
                {
                    case "RSERVE":
                    case "FILE":
                        RserveToken = new CancellationTokenSource();
                        Task.Factory.StartNew(ReConnectToRserve, RserveToken.Token,
                                              TaskCreationOptions.LongRunning, TaskScheduler.Default);

                        break;

                    default:
                        throw new Exception($"The uri scheme \"{Parameter.Scheme}\" is unknown.");
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"R connection could not be established: {ex.Message}");
            }
        }

        public void Dispose()
        {
            RserveToken?.Cancel();
            RTermToken?.Cancel();

            if (RtermProcess != null)
            {
                Connection?.Dispose();
                RtermProcess?.Kill();
                RtermProcess?.Dispose();
            }
            else
            {
                Connection?.Dispose();
            }

            
        }
        #endregion

        #region Private Methods
        private Process StartPServeWithRTerm()
        {
            try
            {
                //--vanilla  Combine 
                //  --no-save, --no-restore, --no-site-file,
                //  --no-init-file --no-environ

                // TODO: maybe complete clear enviroment rm(list=ls())
                // after Rserve Connect

                var arguments = new StringBuilder();
                arguments.Append(Parameter.RProcessCommandLineArgs);
                // arguments.Append($"--vanilla -e \"library(Rserve); ");
                // arguments.Append($"Rserve(port = {Parameter.Port}, wait = TRUE);\"");

                ProcessLog.Clear();
                var rtermProcess = new Process();
                rtermProcess.StartInfo.FileName = Parameter.RTermPath;
                rtermProcess.StartInfo.Arguments = arguments.ToString();
                rtermProcess.StartInfo.RedirectStandardOutput = true;
                rtermProcess.OutputDataReceived += RtermProcess_OutputDataReceived;
                rtermProcess.StartInfo.UseShellExecute = false;
                rtermProcess.StartInfo.CreateNoWindow = true;
                rtermProcess.Start();
                rtermProcess.BeginOutputReadLine();
                rtermProcess.WaitForExit(3000);

                logger.Debug($"rProcess started, Output: {ProcessLog.ToString()}");

                return rtermProcess;
            }
            catch (Exception ex)
            {
                logger.Error($"Error when starting R process: {ex.Message}");
                return null;
            }
        }

        private void RtermProcess_OutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            ProcessLog.Append(e.Data);
        }

        private void FindRTermProcess()
        {
            while (RTermToken != null && !RTermToken.IsCancellationRequested)
            {
                if (RtermProcess == null)
                {
                    RtermProcess = StartPServeWithRTerm();
                }
                else if (RtermProcess.HasExited)
                {
                    logger.Debug($"rProcess terminated at {RtermProcess.ExitTime}, exitcode: {RtermProcess.ExitCode}");
                    // Try to start the process again, but wait a little bit before trying since it could be a normal shutdown
                    Thread.Sleep(10000);
                    RtermProcess?.Dispose();
                    RtermProcess = null;
                }

                Thread.Sleep(2500);
            }
        }

        private void ReConnectToRserve()
        {
            while (RserveToken != null && !RserveToken.IsCancellationRequested)
            {
                try
                {
                    if (Connection == null)
                    {
                        string userLogString = "empty user";
                        if (Parameter.Credentials != null)
                        {
                            userLogString = Parameter.User;
                        }

                        RConnection con = null;
                        if (Parameter.UseIpAddress)
                        {
                            con = RConnection.Connect(Parameter.IpAddress, Parameter.Port, Parameter.Credentials);
                            logger.Info($"Connected to RServe {Parameter.IpAddress}:{Parameter.Port} with user ({userLogString})");
                        }
                        else
                        {
                            con = RConnection.Connect(Parameter.Hostname, Parameter.Port, Parameter.Credentials);
                            logger.Info($"Connected to RServe {Parameter.Hostname}:{Parameter.Port} with user ({userLogString})");
                        }
                        Thread.Sleep(150);
                        
                        //Load Workspace
                        if (!String.IsNullOrWhiteSpace(Parameter.InitScript))
                        {
                            logger.Debug("Sending InitScript to Rserve...");
                            con.Eval(Parameter.InitScript);
                            logger.Debug("...InitScript done");
                        }
                        Connection = con;
                    }

                    if (!Connection.IsConnected())
                    {
                        Connection = null;
                    }
                }
                catch (SocketException ex)
                {
                    logger.Error($"Reconnect to Rserve failed (Socket error): {ex.Message}");
                    Connection = null;
                }
                catch (Exception ex)
                {
                    logger.Error($"Reconnect to Rserve failed: {ex.Message}");
                    Connection = null;
                }
                finally
                {
                    Thread.Sleep(5000);
                }
            }
        }
        #endregion
        
    }

    public static class RConnectionExtension
    {
        private static FieldInfo fi_socket = null;

        public static bool IsConnected(this RConnection rconnection)
        {
            if (fi_socket == null)
                fi_socket = typeof(RConnection).GetField("_socket", BindingFlags.NonPublic | BindingFlags.Instance);

            return ((fi_socket.GetValue(rconnection) as Socket)?.Connected ?? false);
        }
    }

}