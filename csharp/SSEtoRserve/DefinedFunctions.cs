using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Newtonsoft.Json;
using NLog;
using Qlik.Sse;
using System.ComponentModel;

namespace SSEtoRserve
{
    class DefinedFunctions
    {
        private static Logger logger = LogManager.GetCurrentClassLogger();
        
        public class Function
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public int FunctionType { get; set; }
            public int ReturnType { get; set; }

            [DefaultValue(true)]
            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
            public bool CacheResultInQlik { get; set; }
            public string FunctionRScript { get; set; }
            public Dictionary<string, int> Params { get; set; }
        }
        public class FuncDefs
        {
            public IList<Function> functions { get; set; }
        }

        public FuncDefs funcDefs = null;
        public IList<Qlik.Sse.FunctionDefinition> sseFunctions = null;
        private int[] funcIdList = null;
        public DefinedFunctions(string functionDefinitionsFile)
        {
            try
            {
                funcDefs = JsonConvert.DeserializeObject<FuncDefs>(File.ReadAllText(functionDefinitionsFile));
                if (!CreateUniqueFuncIdList())
                    throw new Exception($"Id in defined function is not unique.");

                sseFunctions = new List<Qlik.Sse.FunctionDefinition>();
                foreach (Function func in funcDefs.functions)
                {
                    if (!Enum.IsDefined(typeof(DataType), func.ReturnType))
                        throw new Exception($"Invalid ReturnType in FunctionId: {func.Id}");
                    if (!Enum.IsDefined(typeof(FunctionType), func.FunctionType))
                        throw new Exception($"Invalid FunctionType in FunctionId: {func.Id}");
                    if (string.IsNullOrEmpty(func.Name))
                        throw new Exception($"Invalid Name in FunctionId: {func.Id}");
                    IList<Qlik.Sse.Parameter>  funcParams = new List<Qlik.Sse.Parameter>();
                    foreach (var par in func.Params)
                    {
                        if (!Enum.IsDefined(typeof(DataType), par.Value))
                            throw new Exception($"Invalid Params DataType in FunctionId: {func.Id}");
                        funcParams.Add(new Qlik.Sse.Parameter
                        {
                            Name = par.Key,
                            DataType = (DataType)(par.Value)
                        });
                    }
                    sseFunctions.Add(new Qlik.Sse.FunctionDefinition 
                    {
                        FunctionId = func.Id,
                        FunctionType = (FunctionType)func.FunctionType,
                        Name = func.Name,
                        Params = { funcParams },
                        ReturnType = (DataType)func.ReturnType
                    });
                    logger.Info($"Added defined function, Name:{func.Name}, Id:{func.Id}, FunctionType:{(FunctionType)func.FunctionType}, ReturnType:{(DataType)func.ReturnType}");
                }
            }
            catch (Exception ex)
            {
                logger.Error($"Exception when reading FuncDefs.json: {ex.Message}");
                throw ex;
            }
        }

        private bool CreateUniqueFuncIdList()
        {
            if (funcDefs.functions == null || funcDefs.functions.Count() == 0)
                return false;

            funcIdList = new int[funcDefs.functions.Count];
            for (int i = 0; i < funcIdList.Length; i++)
            {
                funcIdList[i] = funcDefs.functions[i].Id;
            }
            return funcIdList.Distinct().Count() == funcIdList.Length;
        }
        /// <summary>
        //// Returns the index in funcIdList containg the id. Returns -1 if id not found.
        /// </summary>
        public int GetIndexOfFuncId(int id)
        {
            if (funcIdList != null && funcIdList.Count() > 0)
            {
                return Array.IndexOf(funcIdList, id);
            }
            else
            {
                return (-1);
            }
        }

    }
}
