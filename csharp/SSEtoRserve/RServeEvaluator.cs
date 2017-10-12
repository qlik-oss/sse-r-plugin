using Grpc.Core;
using Qlik.Sse;
using RserveCLI2;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using static Qlik.Sse.Connector;
using NLog;

namespace SSEtoRserve
{
    class RServeEvaluator : ConnectorBase, IDisposable
    {
        private static SemaphoreSlim semaphore = new SemaphoreSlim(1);
        private static Logger logger = LogManager.GetCurrentClassLogger();

        public class ParameterData
        {
            public DataType DataType;
            public List<double> Numerics;
            public List<string> Strings;
        }

        #region Properties and Variables
        private RserveConnectionPool connPool;
        private RserveParameter rservePara;
        #endregion

        #region Constructor & Dispose
        public RServeEvaluator(RserveParameter para)
        {
            connPool = new RserveConnectionPool();
            rservePara = para;
            RserveConnection rserveConnInitial = connPool.GetConnection(rservePara);

            logger.Trace($"Rserve connection initiated: {rserveConnInitial!=null}");
        }

        public void Dispose()
        {
            connPool?.Dispose();
        }
        #endregion

        public override Task<Capabilities> GetCapabilities(global::Qlik.Sse.Empty request, ServerCallContext context)
        {
            try
            {
                var identifier = $"Qlik SSEtoRserve plugin";
                var version = $"v1.0.0";
                logger.Info($"GetCapabilities called, returned ({identifier}), version ({version})");

                return Task.FromResult(new Capabilities
                {
                    AllowScript = true,
                    PluginIdentifier = identifier,
                    PluginVersion = version
                });
            }
            catch (Exception ex)
            {
                logger.Error($"GetCapabilities failed: {ex.Message}");
                return null;
            }
    }
        private string[] GetParamNames(Parameter[] Parameters)
        {
            return Parameters
                        .Select((_, index) => string.Format($"arg{index + 1}"))
                        .ToArray();
        }

        ParameterData[] GetParams(Parameter[] Parameters)
        {
            return Parameters
                        .Select((Param) =>
                        {
                            var p = new ParameterData()
                            {
                                DataType = Param.DataType
                            };
                            switch (Param.DataType)
                            {
                                case DataType.Numeric:
                                    p.Numerics = new List<double>();
                                    break;
                                case DataType.String:
                                    p.Strings = new List<string>();
                                    break;
                                case DataType.Dual:
                                default:
                                    throw new NotImplementedException();
                            }
                            return p;
                        })
                        .ToArray();

        }

        async Task ConvertToColumnar(ParameterData[] Parameters, IAsyncStreamReader<global::Qlik.Sse.BundledRows> requestStream)
        {
            int rowNum = 0;
            while (await requestStream.MoveNext())
            {
                var bundledRows = requestStream.Current;
                var nrOfRows = bundledRows.Rows.Count;
                for (int r = 0; r < nrOfRows; r++)
                {
                    var Row = bundledRows.Rows[r];
                    var logRowData = new List<string>();
                    for (int i = 0; i < Parameters.Length; i++)
                    {
                        var param = Parameters[i];
                        var dual = Row.Duals[i];
                        switch (param.DataType)
                        {
                            case DataType.Numeric:
                                param.Numerics.Add(dual.NumData);
                                logRowData.Add($"{dual.NumData}");
                                break;
                            case DataType.String:
                                param.Strings.Add(dual.StrData);
                                logRowData.Add($"{dual.StrData}");
                                break;
                            case DataType.Dual:
                                // Dual not supported by R take numerical value
                                param.Numerics.Add(dual.NumData);
                                logRowData.Add($"{dual.NumData}");
                                break;
                        }
                    }
                    if (logger.IsTraceEnabled)
                    {
                        var logRowDataStr = String.Join(", ", logRowData);
                        logger.Trace("Incoming data row[{0}]: {1}", rowNum, logRowDataStr);
                        rowNum++;
                    }
                }
            }

        }

        async Task AddInputData(Parameter[] Parameters, IAsyncStreamReader<global::Qlik.Sse.BundledRows> requestStream, RserveConnection rserveConn)
        {
            var Params = GetParams(Parameters);
            await ConvertToColumnar(Params, requestStream);
            var data = new List<KeyValuePair<string, object>>();
            for (int i = 0; i < Params.Length; i++)
            {
                var s = GenerateData(Params[i]);
                data.Add(new KeyValuePair<string, object>(Parameters[i].Name, s));
            }
            rserveConn.Connection["q"] = Sexp.MakeDataFrame(data);
        }

        private Sexp GenerateData(ParameterData Parameter)
        {
            switch (Parameter.DataType)
            {
                case DataType.Numeric:
                    return Sexp.Make(Parameter.Numerics);
                case DataType.String:
                    return Sexp.Make(Parameter.Strings);
                case DataType.Dual:
                    return Sexp.Make(Parameter.Numerics);
                default:
                    throw new NotImplementedException();
            }
        }

        byte[] GetHeader(Metadata Headers, string Key)
        {
            foreach(var Header in Headers)
            {
                if(Header.Key == Key)
                {
                    return Header.ValueBytes;
                }
            }
            return null;
        }
        public override async global::System.Threading.Tasks.Task EvaluateScript(IAsyncStreamReader<global::Qlik.Sse.BundledRows> requestStream, IServerStreamWriter<global::Qlik.Sse.BundledRows> responseStream, ServerCallContext context)
        {
            ScriptRequestHeader scriptHeader;
            RserveConnection rserveConn;

            try
            {
                rserveConn = connPool.GetConnection(rservePara);

                var header = GetHeader(context.RequestHeaders, "qlik-scriptrequestheader-bin");
                scriptHeader = ScriptRequestHeader.Parser.ParseFrom(header);
            }
            catch (Exception e)
            {
                logger.Error($"EvaluateScript failed: {e.Message}");
                throw new RpcException(new Status(StatusCode.DataLoss, e.Message));
            }

            try
            {
                var stopwatch = new Stopwatch();
                stopwatch.Start();

                logger.Info($"Evaluating {scriptHeader.Script}");
                var paramnames = "Param names: ";
                foreach (var param in scriptHeader.Params)
                {
                    paramnames += $" {param.Name}";
                }
                logger.Info("{0}", paramnames);
                if (scriptHeader.Params != null && scriptHeader.Params.Count > 0)
                {
                    await AddInputData(scriptHeader.Params.ToArray(), requestStream, rserveConn);
                }
                var res = rserveConn.Connection.Eval(scriptHeader.Script);
                logger.Info($"Rserve result: {res.Count} rows");
                
                // Disable caching (uncomment line below if you do not want the results sent to Qlik to be cached in Qlik)
                // await context.WriteResponseHeadersAsync(new Metadata { { "qlik-cache", "no-store" } });

                await GenerateResult(res, responseStream, rserveConn);
                stopwatch.Stop();
                logger.Debug($"Took {stopwatch.ElapsedMilliseconds} ms");
            }
            catch (Exception e)
            {
                HandleError(e, rserveConn);
            }
            finally
            {
               // 
            }
        }

        private void HandleError (Exception ex, RserveConnection rserveConn)
        {
            String msg;
            try
            {
                msg = rserveConn.Connection.Eval("geterrmessage()").AsString;
                if (String.IsNullOrWhiteSpace(msg))
                {
                    msg = ex.Message;
                }

                logger.Warn("Rserve error: {0}", msg);
            }
            catch
            {
                logger.Warn("Rserve error: {0}", ex.Message);
                throw new RpcException(new Status(StatusCode.Unavailable, $"Rserve error: {ex.Message}"));
            }

            // Try to get the stack trace
            // It's possible that geterrmessage() succeeds and traceback() fails.
            try
            {
                var tracebacks = rserveConn.Connection.Eval("traceback()").AsStrings;
                var traceback = String.Join("\r\n", tracebacks);
                logger.Warn("Rserve Traceback: {0}", traceback);
            }
            catch
            {
                // Error msg already logged
            }           
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"Rserve error: {msg}"));
        }
        private void HandleZeroRowsFromRserve(RserveConnection rserveConn)
        {
            String msg = $"No data returned from R script execution. Possible error in script: ";
            try
            {
                String errMsg = rserveConn.Connection.Eval("geterrmessage()").AsString;
                if (!String.IsNullOrWhiteSpace(errMsg))
                {
                    msg = msg + errMsg;
                }

                logger.Warn("{0}", msg);
            }
            catch
            {
                throw new RpcException(new Status(StatusCode.Unknown, $"{msg}"));
            }

            // Try to get the stack trace
            // It's possible that geterrmessage() succeeds and traceback() fails.
            try
            {
                var tracebacks = rserveConn.Connection.Eval("traceback()").AsStrings;
                var traceback = String.Join("\r\n", tracebacks);
                logger.Warn("Rserve Traceback when no data returned from R script execution: {0}", traceback);
            }
            catch
            {
                // Error msg already logged
            }
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"{msg}"));
        }
        private async Task GenerateResult(Sexp RResult, IServerStreamWriter<global::Qlik.Sse.BundledRows> responseStream, RserveConnection rserveConn)
        {
            if (RResult is SexpArrayBool || RResult is SexpArrayDouble || RResult is SexpArrayInt)
            {
                var bundledRows = new BundledRows();
                var numerics = RResult.AsDoubles;
                if (numerics.Length == 0)
                {
                    HandleZeroRowsFromRserve(rserveConn);
                }

                for (int i=0; i< numerics.Length; i++)
                {
                    var row = new Row();
                    row.Duals.Add(new Dual() { NumData = numerics[i] });
                    bundledRows.Rows.Add(row);
                    if ((i % 2000) == 0)
                    {
                        // Send a bundle
                        await responseStream.WriteAsync(bundledRows);
                        bundledRows = new BundledRows();
                    }
                }

                if (bundledRows.Rows.Count() > 0)
                {
                    // Send last bundle
                    await responseStream.WriteAsync(bundledRows);
                }

                if (logger.IsTraceEnabled)
                {
                    var logNumerics = String.Join(", ", numerics);
                    logger.Trace("Numeric result column data: {0}", logNumerics);
                }
            }
            else if (RResult is SexpArrayString)
            {
                var bundledRows = new BundledRows();
                var strings = RResult.AsStrings;
                if (strings.Length == 0)
                {
                    HandleZeroRowsFromRserve(rserveConn);
                }

                for (int i = 0; i < strings.Length; i++)
                {
                    var row = new Row();
                    row.Duals.Add(new Dual() {  StrData = strings[i]??"" });
                    bundledRows.Rows.Add(row);
                    if ((i % 2000) == 0)
                    {
                        // Send a bundle
                        await responseStream.WriteAsync(bundledRows);
                        bundledRows = new BundledRows();
                    }
                }

                if (bundledRows.Rows.Count() > 0)
                {
                    // Send last bundle
                    await responseStream.WriteAsync(bundledRows);
                }

                if (logger.IsTraceEnabled)
                {
                    var logStrings = String.Join(", ", strings);
                    logger.Trace("String result column data: {0}", logStrings);
                }
            }
            else
            {
                throw new NotImplementedException();
            }
        }
    }

}
