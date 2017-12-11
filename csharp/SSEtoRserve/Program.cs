using Grpc.Core;
using Qlik.Sse;
using System;
using System.Collections.Generic;
using System.Net;
using NLog;
using System.IO;

namespace SSEtoRserve
{
    class Program
    {
        private static Logger logger = LogManager.GetCurrentClassLogger();
        static void Main(string[] args)
        {
            try
            {
                var grpcHost = Convert.ToString(Properties.Settings.Default.grpcHost ?? "localhost");
                int grpcPort = Convert.ToInt32(Properties.Settings.Default.grpcPort ?? "50051");
                var rserveHost = IPAddress.Parse(Properties.Settings.Default.rserveHost ?? "127.0.0.1");
                int rservePort = Convert.ToInt32(Properties.Settings.Default.rservePort ?? "6311");
                var rserveUser = Convert.ToString(Properties.Settings.Default.rserveUser ?? "");
                var rservePassword = Convert.ToString(Properties.Settings.Default.rservePassword ?? "");
                var rProcessPath = Convert.ToString(Properties.Settings.Default.rProcessPathToStart ?? "");
                var rProcessCommandLineArgs = Convert.ToString(Properties.Settings.Default.rProcessCommandLineArgs ?? "");
                var rserveInitScript = Convert.ToString(Properties.Settings.Default.rserveInitScript ?? "");
                bool allowScript = Convert.ToBoolean(Properties.Settings.Default.allowScript);
                var functionDefinitionsFile = Convert.ToString(Properties.Settings.Default.functionDefinitionsFile ?? "");

                var sslCredentials = ServerCredentials.Insecure;
                var certificateFolderFullPath = Convert.ToString(Properties.Settings.Default.certificateFolderFullPath ?? "");

                if (certificateFolderFullPath.Length > 3)
                {
                    var rootCertPath = Path.Combine(certificateFolderFullPath, @"root_cert.pem");
                    var serverCertPath = Path.Combine(certificateFolderFullPath, @"sse_server_cert.pem");
                    var serverKeyPath = Path.Combine(certificateFolderFullPath, @"sse_server_key.pem");
                    if (File.Exists(rootCertPath) &&
                        File.Exists(serverCertPath) &&
                        File.Exists(serverKeyPath))
                    {
                        var rootCert = File.ReadAllText(rootCertPath);
                        var serverCert = File.ReadAllText(serverCertPath);
                        var serverKey = File.ReadAllText(serverKeyPath);
                        var serverKeyPair = new KeyCertificatePair(serverCert, serverKey);
                        sslCredentials = new SslServerCredentials(new List<KeyCertificatePair>() { serverKeyPair }, rootCert, true);
                        logger.Info($"Path to certificates ({certificateFolderFullPath}) and certificate files found. Opening secure channel with mutual authentication.");
                    }
                    else
                    {
                        logger.Warn($"Path to certificates ({certificateFolderFullPath}) not found or files missing. Opening insecure channel instead.");
                    }
                }
                else
                {
                    logger.Info("No certificates defined. Opening insecure channel.");
                }

                var uri = new Uri($"rserve://{rserveHost}:{rservePort}");
                if (!String.IsNullOrEmpty(rProcessPath))
                    uri = new Uri(rProcessPath);
                var parameter = new RserveParameter(uri, rservePort, rserveInitScript, rProcessCommandLineArgs, rserveUser, rservePassword);

                using (var rServeEvaluator = new RServeEvaluator(parameter, allowScript, functionDefinitionsFile))
                {
                    var server = new Server
                    {
                        Services = { Connector.BindService(rServeEvaluator) },
                        Ports = { new ServerPort(grpcHost, grpcPort, sslCredentials) }
                    };

                    server.Start();
                    Console.WriteLine("Press any key to stop SSEtoRserve...");
                    logger.Info($"gRPC listening on port {grpcPort}");
                    Console.ReadKey();
                    logger.Info("Shutting down SSEtoRserve... Bye!");
                    server?.ShutdownAsync().Wait();
                    rServeEvaluator?.Dispose();
                }
            }
            catch (Exception ex)
            {
                logger.Error($"Error in main entry point of SSEtoRserve: {ex}");
            }
        }
    }
}
