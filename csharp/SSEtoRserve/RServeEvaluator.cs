using Grpc.Core;
using Qlik.Sse;
using RserveCLI2;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
        private DefinedFunctions definedFunctions;
        private Qlik.Sse.Capabilities capabilities;
        private int nrOfDefinedFunctions;
        bool allowScript = false;
        #endregion

        #region Constructor & Dispose
        public RServeEvaluator(RserveParameter para, bool enableScript, string functionDefinitionsFile)
        {
            allowScript = enableScript;
            if (!String.IsNullOrEmpty(functionDefinitionsFile))
            {
                definedFunctions = new DefinedFunctions(functionDefinitionsFile);
            }
            CreateCapabilities();
            connPool = new RserveConnectionPool();
            rservePara = para;
            RserveConnection rserveConnInitial = connPool.GetConnection(rservePara);

            logger.Trace($"Rserve connection initiated: {rserveConnInitial!=null}");
        }

        private void CreateCapabilities()
        {
            try
            {
                var identifier = $"Qlik SSEtoRserve plugin";
                var version = $"v1.1.0";
                string registeredFunctionsString = $"No functions defined";

                capabilities = new Capabilities
                {
                    AllowScript = allowScript,
                    PluginIdentifier = identifier,
                    PluginVersion = version
                };

                nrOfDefinedFunctions = 0;

                if (definedFunctions?.sseFunctions?.Count > 0)
                {
                    nrOfDefinedFunctions = definedFunctions.sseFunctions.Count;
                    registeredFunctionsString = $"{nrOfDefinedFunctions} Functions defined";
                    capabilities.Functions.AddRange(definedFunctions.sseFunctions);
                }

                logger.Info($"Capabilities created: identifier ({identifier}), version ({version}), allowScript ({allowScript}), defined functions ({registeredFunctionsString})");
            }
            catch (Exception ex)
            {
                logger.Error($"Failed to create Capabilities: {ex.Message}");
                throw ex;
            }
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
                logger.Info($"GetCapabilities called from client ({context.Peer})");

                return Task.FromResult(capabilities);
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

        public override async Task ExecuteFunction(IAsyncStreamReader<BundledRows> requestStream, IServerStreamWriter<BundledRows> responseStream, ServerCallContext context)
        {
            FunctionRequestHeader functionHeader;
            CommonRequestHeader commonHeader;
            RserveConnection rserveConn;
            int reqHash = requestStream.GetHashCode();
            Qlik.Sse.FunctionDefinition sseFunction;
            DefinedFunctions.Function internalFunction;

            if (nrOfDefinedFunctions == 0)
            {
                throw new RpcException(new Status(StatusCode.Unimplemented, $"No functions defined"));
            }

            try
            {
                rserveConn = connPool.GetConnection(rservePara);

                var header = GetHeader(context.RequestHeaders, "qlik-functionrequestheader-bin");
                functionHeader = FunctionRequestHeader.Parser.ParseFrom(header);

                var commonRequestHeader = GetHeader(context.RequestHeaders, "qlik-commonrequestheader-bin");
                commonHeader = CommonRequestHeader.Parser.ParseFrom(commonRequestHeader);

                logger.Info($"ExecuteFunction: FunctionId ({functionHeader.FunctionId}), from client ({context.Peer}), hashid ({reqHash})");
                logger.Debug($"ExecuteFunction header info: AppId ({commonHeader.AppId}), UserId ({commonHeader.UserId}), Cardinality ({commonHeader.Cardinality} rows)");

                int funcIndex = definedFunctions.GetIndexOfFuncId(functionHeader.FunctionId);
                if (funcIndex < 0)
                {
                    throw new Exception($"FunctionId ({functionHeader.FunctionId}) is not a defined function");
                }
                sseFunction = definedFunctions.sseFunctions[funcIndex];
                internalFunction = definedFunctions.funcDefs.functions[funcIndex];
            }
            catch (Exception e)
            {
                logger.Error($"ExecuteFunction with hashid ({reqHash}) failed: {e.Message}");
                throw new RpcException(new Status(StatusCode.DataLoss, e.Message));
            }

            try
            {
                var stopwatch = new Stopwatch();
                stopwatch.Start();

                await AddInputData(sseFunction.Params.ToArray(), requestStream, rserveConn);

                logger.Debug($"Evaluating R script: {internalFunction.FunctionRScript}");
                var res = rserveConn.Connection.Eval(internalFunction.FunctionRScript);
                logger.Info($"Rserve result: {res.Count} rows, hashid ({reqHash})");

                if (!internalFunction.CacheResultInQlik)
                {
                    await context.WriteResponseHeadersAsync(new Metadata { { "qlik-cache", "no-store" } });
                }

                await GenerateResult(res, responseStream, rserveConn);
                stopwatch.Stop();
                logger.Debug($"Took {stopwatch.ElapsedMilliseconds} ms, hashid ({reqHash})");
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
        public override async Task EvaluateScript(IAsyncStreamReader<global::Qlik.Sse.BundledRows> requestStream, IServerStreamWriter<global::Qlik.Sse.BundledRows> responseStream, ServerCallContext context)
        {
            ScriptRequestHeader scriptHeader;
            CommonRequestHeader commonHeader;
            RserveConnection rserveConn;
            int reqHash = requestStream.GetHashCode();

            if (!(capabilities.AllowScript))
            {
                throw new RpcException(new Status(StatusCode.PermissionDenied, $"Script evaluations disabled"));
            }

            try
            {
                rserveConn = connPool.GetConnection(rservePara);

                var header = GetHeader(context.RequestHeaders, "qlik-scriptrequestheader-bin");
                scriptHeader = ScriptRequestHeader.Parser.ParseFrom(header);

                var commonRequestHeader = GetHeader(context.RequestHeaders, "qlik-commonrequestheader-bin");
                commonHeader = CommonRequestHeader.Parser.ParseFrom(commonRequestHeader);

                logger.Info($"EvaluateScript called from client ({context.Peer}), hashid ({reqHash})");
                logger.Debug($"EvaluateScript header info: AppId ({commonHeader.AppId}), UserId ({commonHeader.UserId}), Cardinality ({commonHeader.Cardinality} rows)");
            }
            catch (Exception e)
            {
                logger.Error($"EvaluateScript with hashid ({reqHash}) failed: {e.Message}");
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
                logger.Info($"Rserve result: {res.Count} rows, hashid ({reqHash})");
                
                // Disable caching (uncomment line below if you do not want the results sent to Qlik to be cached in Qlik)
                // await context.WriteResponseHeadersAsync(new Metadata { { "qlik-cache", "no-store" } });

                await GenerateResult(res, responseStream, rserveConn);
                stopwatch.Stop();
                logger.Debug($"Took {stopwatch.ElapsedMilliseconds} ms, hashid ({reqHash})");
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
