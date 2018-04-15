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
using Google.Protobuf;

namespace SSEtoRserve
{
    class RServeEvaluator : ConnectorBase, IDisposable
    {
        private static SemaphoreSlim semaphoreRserve = new SemaphoreSlim(1, 1);
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

            logger.Trace($"Rserve connection initiated: {rserveConnInitial != null}");
        }

        private void CreateCapabilities()
        {
            try
            {
                var identifier = $"Qlik SSEtoRserve plugin";
                var version = $"v1.2.1";
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

        async Task<SexpList> AddInputData(Parameter[] Parameters, IAsyncStreamReader<global::Qlik.Sse.BundledRows> requestStream)
        {
            var Params = GetParams(Parameters);
            await ConvertToColumnar(Params, requestStream);
            var data = new List<KeyValuePair<string, object>>();

            for (int i = 0; i < Params.Length; i++)
            {
                var s = GenerateData(Params[i]);
                data.Add(new KeyValuePair<string, object>(Parameters[i].Name, s));
            }
            return Sexp.MakeDataFrame(data);
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
            foreach (var Header in Headers)
            {
                if (Header.Key == Key)
                {
                    return Header.ValueBytes;
                }
            }
            return null;
        }

        async Task<Sexp> EvaluateScriptInRserve(SexpList inputDataFrame, int reqHash, string rScript, RserveConnection rserveConn)
        {
            await semaphoreRserve.WaitAsync();
            try
            {
                if (inputDataFrame != null && inputDataFrame.Count > 0)
                {
                    rserveConn.Connection["q"] = inputDataFrame;
                }
                logger.Debug($"Evaluating R script, hashid ({reqHash}): {rScript}");

                var res = rserveConn.Connection.Eval(rScript);
                logger.Info($"Rserve result: {res.Count} rows, hashid ({reqHash})");
                if (res.Count == 0)
                {
                    HandleZeroRowsFromRserve(rserveConn);
                }

                return res;
            }
            catch (Exception e)
            {
                HandleError(e, rserveConn);
                return null;
            }
            finally
            {
                semaphoreRserve.Release();
            }
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

                SexpList inputDataFrame = null;

                if (sseFunction.Params.Count > 0)
                {
                    inputDataFrame = await AddInputData(sseFunction.Params.ToArray(), requestStream);
                }

                var rResult = await EvaluateScriptInRserve(inputDataFrame, reqHash, internalFunction.FunctionRScript.Replace("\r", " "), rserveConn);

                await GenerateResult(rResult, responseStream, context, true, sseFunction.ReturnType, internalFunction.CacheResultInQlik);
                stopwatch.Stop();
                logger.Debug($"Took {stopwatch.ElapsedMilliseconds} ms, hashid ({reqHash})");
            }
            catch (Exception e)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, $"{e.Message}"));
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

                var paramnames = $"EvaluateScript call with hashid({reqHash}) got Param names: ";

                foreach (var param in scriptHeader.Params)
                {
                    paramnames += $" {param.Name}";
                }
                logger.Info("{0}", paramnames);

                SexpList inputDataFrame = null;

                if (scriptHeader.Params != null && scriptHeader.Params.Count > 0)
                {
                    inputDataFrame = await AddInputData(scriptHeader.Params.ToArray(), requestStream);
                }

                var rResult = await EvaluateScriptInRserve(inputDataFrame, reqHash, scriptHeader.Script.Replace("\r", " "), rserveConn);

                // Disable caching (uncomment line below and comment next line if you do not want the results sent to Qlik to be cached in Qlik)
                //await GenerateResult(rResult, responseStream, context, cacheResultInQlik: false);
                await GenerateResult(rResult, responseStream, context);
                stopwatch.Stop();
                logger.Debug($"Took {stopwatch.ElapsedMilliseconds} ms, hashid ({reqHash})");
            }
            catch (Exception e)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, $"{e.Message}"));
            }
            finally
            {
                // 
            }
        }

        private void HandleError(Exception ex, RserveConnection rserveConn)
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

        public class ResultDataColumn
        {
            public string Name;
            public DataType DataType;
            public double[] Numerics;
            public string[] Strings;
        }

        private async Task GenerateResult(Sexp RResult, IServerStreamWriter<global::Qlik.Sse.BundledRows> responseStream, ServerCallContext context,
            bool failIfWrongDataTypeInFirstCol = false, DataType expectedFirstDataType = DataType.Numeric, bool cacheResultInQlik = true)
        {
            int nrOfCols = 0;
            int nrOfRows = 0;
            ResultDataColumn[] resultDataColumns = null;
            var names = RResult.Names;

            if (names != null)
            {
                logger.Debug($"Rserve result column names: {String.Join(", ", names)}");
            }

            if (RResult is SexpList)
            {
                // Indicating this is a data.frame/matrix response structure. Figure out how many columns, names and data types
                nrOfCols = RResult.Count;
                logger.Debug($"Rserve result nrOfColumns: {nrOfCols}");
                if (RResult.Attributes != null && RResult.Attributes.Count > 0)
                {
                    Sexp resObjectNames;

                    if ((names == null || names.Length == 0) && RResult.Attributes.TryGetValue("names", out resObjectNames))
                    {
                        names = resObjectNames.AsStrings;
                        logger.Debug($"Rserve result column names: {String.Join(", ", names)}");
                    }

                    Sexp resObjectClass;

                    if (RResult.Attributes.TryGetValue("class", out resObjectClass))
                    {
                        logger.Debug($"Rserve result object class: {resObjectClass.ToString()}");
                    }
                }
                if (nrOfCols > 0)
                {
                    var columns = RResult.AsList;
                    resultDataColumns = GetResultDataColumns(ref nrOfRows, names, columns);
                }
            }
            else if (RResult is SexpArrayBool || RResult is SexpArrayDouble || RResult is SexpArrayInt)
            {
                nrOfCols = 1;
                var bundledRows = new BundledRows();
                var numerics = RResult.AsDoubles;
                nrOfRows = numerics.Length;

                var c = new ResultDataColumn();
                c.Name = "";
                c.DataType = DataType.Numeric;
                c.Numerics = numerics;
                resultDataColumns = new ResultDataColumn[1];
                resultDataColumns[0] = c;

                if (logger.IsTraceEnabled)
                {
                    var logNumerics = String.Join(", ", numerics);
                    logger.Trace("Numeric result column data[0]: {0}", logNumerics);
                }
            }
            else if (RResult is SexpArrayString)
            {
                nrOfCols = 1;
                var bundledRows = new BundledRows();
                var strings = RResult.AsStrings;
                nrOfRows = strings.Length;

                var c = new ResultDataColumn();
                c.Name = "";
                c.DataType = DataType.String;
                c.Strings = strings;
                resultDataColumns = new ResultDataColumn[1];
                resultDataColumns[0] = c;

                if (logger.IsTraceEnabled)
                {
                    var logStrings = String.Join(", ", strings);
                    logger.Trace("String result column data[0]: {0}", logStrings);
                }
            }
            else
            {
                logger.Warn($"Rserve result, column data type not recognized: {RResult.GetType().ToString()}");
                throw new NotImplementedException();
            }

            if (resultDataColumns != null)
            {
                if (failIfWrongDataTypeInFirstCol && expectedFirstDataType != resultDataColumns[0].DataType)
                {
                    string msg = $"Rserve result datatype mismatch in first column, expected {expectedFirstDataType}, got {resultDataColumns[0].DataType}";
                    logger.Warn($"{msg}");
                    throw new RpcException(new Status(StatusCode.InvalidArgument, $"{msg}"));
                }

                //Send TableDescription header
                TableDescription tableDesc = new TableDescription
                {
                    NumberOfRows = nrOfRows
                };

                for (int col = 0; col < nrOfCols; col++)
                {
                    if (String.IsNullOrEmpty(resultDataColumns[col].Name))
                    {
                        tableDesc.Fields.Add(new FieldDescription
                        {
                            DataType = resultDataColumns[col].DataType
                        });
                    }
                    else
                    {
                        tableDesc.Fields.Add(new FieldDescription
                        {
                            DataType = resultDataColumns[col].DataType,
                            Name = resultDataColumns[col].Name
                        });
                    }
                }

                var tableMetadata = new Metadata
                {
                    { new Metadata.Entry("qlik-tabledescription-bin", MessageExtensions.ToByteArray(tableDesc)) }
                };

                if (!cacheResultInQlik)
                {
                    tableMetadata.Add("qlik-cache", "no-store");
                }

                await context.WriteResponseHeadersAsync(tableMetadata);

                // Send data
                var bundledRows = new BundledRows();

                for (int i = 0; i < nrOfRows; i++)
                {
                    var row = new Row();

                    for (int col = 0; col < nrOfCols; col++)
                    {
                        if (resultDataColumns[col].DataType == DataType.Numeric)
                        {
                            row.Duals.Add(new Dual() { NumData = resultDataColumns[col].Numerics[i] });
                        }
                        else if (resultDataColumns[col].DataType == DataType.String)
                        {
                            row.Duals.Add(new Dual() { StrData = resultDataColumns[col].Strings[i] ?? "" });
                        }
                    }
                    bundledRows.Rows.Add(row);
                    if (((i + 1) % 2000) == 0)
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
            }
        }

        private ResultDataColumn[] GetResultDataColumns(ref int nrOfRows, string[] names, IList<object> columns)
        {
            int nRows = nrOfRows;

            ResultDataColumn[] resultDataColumns = columns
                .Select((col, index) =>
                {
                    var c = new ResultDataColumn();

                    if (names != null && names.Length > 0)
                    {
                        c.Name = names[index] ?? "";
                    }
                    if (col is SexpArrayBool || col is SexpArrayDouble || col is SexpArrayInt)
                    {
                        c.DataType = DataType.Numeric;

                        Sexp colAsSexp = (Sexp)col;
                        c.Numerics = colAsSexp.AsDoubles;
                        if (index == 0)
                        {
                            nRows = c.Numerics.Length;
                        }
                        else if (nRows != c.Numerics.Length)
                        {
                            logger.Warn($"Rserve result, different length in columns: {nRows} vs {c.Numerics.Length}");
                            throw new NotImplementedException();
                        }

                        if (logger.IsTraceEnabled)
                        {
                            var logNumerics = String.Join(", ", c.Numerics);
                            logger.Trace($"Numeric result column data[{index}]: {logNumerics}");
                        }
                    }
                    else if (col is SexpArrayString)
                    {
                        c.DataType = DataType.String;
                        Sexp colAsSexp = (Sexp)col;
                        c.Strings = colAsSexp.AsStrings;
                        if (index == 0)
                        {
                            nRows = c.Strings.Length;
                        }
                        else if (nRows != c.Strings.Length)
                        {
                            logger.Warn($"Rserve result, different length in columns: {nRows} vs {c.Strings.Length}");
                            throw new NotImplementedException();
                        }

                        if (logger.IsTraceEnabled)
                        {
                            var logStrings = String.Join(", ", c.Strings);
                            logger.Trace($"String result column data[{index}]: {logStrings}");
                        }
                    }
                    else
                    {
                        logger.Warn($"Rserve result, column data type not recognized: {col.GetType().ToString()}");
                        throw new NotImplementedException();
                    }
                    return c;
                })
                .ToArray();
            nrOfRows = nRows;
            return resultDataColumns;
        }
    }

}