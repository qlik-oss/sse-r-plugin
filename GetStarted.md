# Build and Run the R-Plugin!

If it is your first time, make sure you set up the R environment and create the executables as described below in the first two steps. Otherwise you can continue directly to [3. Example Sense apps](#3-example-sense-apps).

## 1. Set up R Environment
1. Install R and R Studio by following the instructions on https://www.rstudio.com/
2. Install Rserve:
    1. Start R Studio.
    2. Run `install.packages('Rserve')` in the console.

**Alternative:** Instead of installing R (from https://www.r-project.org/ ) and R Studio, you also have the possibility to use R Open from Microsoft. See https://mran.microsoft.com/open/ for more info.

## 2. Build Project
_Prerequisites_: Visual Studio 2015 (or later)   
To be able to run the _SSEtoRserve_ plugin you must follow these steps:
1. Open the `ServerSideExtension.sln` solution file located in the `csharp` folder.
2. Build the solution to pull down the dependent libraries.
3. Rebuild the solution again. Now you have your executables located in `csharp/SSEtoRserve/bin` folder.

## 3. Example Sense apps
There are several example Sense apps located in the sense_apps folder. Here follows a short description of each and what functionalities are covered as well as dependent libraries.

| __Example__ | __R Libraries__ | __Description__ |
|-----|------|-----|
| __R_DecisionTree__ | rpart, d3r, partykit, jsonlite | A d3.js visualization showing a decision tree, based on a json result string returned from R. |
| __R_TimeSeriesAnalysis__ | TTR, forecast, tseries, colorspace | Time series use cases. |
| __R_BasicExample__ | | Examples for all the different script functions. Also example when calling R from load script. |

## 4. Install Dependent Libraries
Install the dependent libraries for the chosen example by
1. Start R Studio.
2. Run `install.packages('<library>')` for each library that will be needed based on your use case. For the example Qlik Sense apps you can see the needed libraries in the table above.

## 5. Start Rserve
Assuming R Studio is still running. Run the following commands in the console.  
```
library(Rserve)
Rserve()
```

**Alternative:** Instead of manually running R Studio and Rserve, you can configure the SSE R-plugin (SSEtoRserve) to start the Rserve process or Rterm process when starting the plugin, by editing the file `SSEtoRserve.exe.config`. For more information about R plugin config see [Configure SSEtoRserve](#configure-ssetorserve) chapter.   

## 6. Start the R plugin
1. If you have built the plugin from the source code, browse to the `csharp/SSEtoRserve/bin/Release` directory.
2. **[optional]** Configure which Rserve host and port to connect to as well as which gRPC port this plugin should open (the port Qlik Sense is connecting to). This is done by editing the file `SSEtoRserve.exe.config`.
3. **[optional]** Enable secure connection (Mutual Authentication) between this plugin and Qlik, by specifying the certificateFolderFullPath in the file `SSEtoRserve.exe.config`. Three files must be copied manually to that folder and the files needs to have the following names `root_cert.pem`, `sse_server_cert.pem`, `sse_server_key.pem`.
4. **[optional]** Configure the SSEtoRserve log level that is written to the logs folder. Default level is Debug. By enabling Trace level you will see all data that is sent to the plugin from Qlik for easier debugging. This is done by editing the file `NLog.config` (at the bottom of the file). This is also the case for changing the level of console prints.
5. Start `SSEtoRserve.exe` located in the folder.

## 7. Configure the Plugin in Qlik Sense
* __Qlik Sense Desktop__  
Add the following line in the settings.ini file:
`SSEPlugin=R,localhost:50051`
* __Qlik Sense Enterprise/Server__  
  1. In the QMC, add a new Analytic Connection.
  2. Restart the Qlik Sense Engine service.

## 8. Start Qlik Sense and start using the apps
Note that if you make changes to the plugin config in the settings.ini file or in the QMC you need to restart Qlik Sense (Engine service in server) for the changes to take effect since a connection to the plugin is only made during startup.

# Usage from Qlik expressions and load script
Eight script functions are automatically added to the functionality of the plugin. What is needed to be covered on the plugin side to fulfill the functionality is to implement the EvaluateScript rpc function. 

| __Function Name__ | __Function Type__ | __Argument Type__ | __Return Type__ |
| ----- | ----- | ----- | ----- |
| __ScriptEval__ | Scalar, Tensor | Numeric | Numeric |
| __ScriptEvalStr__ | Scalar, Tensor | String | String |
| __ScriptAggr__ | Aggregation | Numeric | Numeric |
| __ScriptAggrStr__ | Aggregation | String | String |

A function call to one of the functions above (either as an expression in an object or in the load script), need to be of the following form.
`<EngineSSEName>.<FunctionName>(Script [,Parameter...])`
Where 
* `<EngineSSEName>` :  Mapping/alias to the plugin, as defined in the configuration.
* `<FunctionName>` : Name of the function.
* `Script`: A string containing the script to be evaluated. 
* `Parameter`: Additional parameters containing data from Qlik. Each data field will be added to the dataframe named q and are  accessible from the R-script above by referencing the q$paramname. 

### Example expression 1
`R.ScriptEvalStr('paste(q$age_b, q$sex);' ,age_b, sex)`
Here we pass two data fields of type string from Qlik (age_b and sex). The R-script then referens those data fields through the q dataframe (q$age_b and q$sex). The script/function returns rows of strings back to Qlik. 

### Example expression 2
`R.ScriptAggr('sum(q$myNumber);' ,num(pclass) as myNumber)`
Here we pass one data field of type numeric from Qlik (num(pclass)). Since the R-script would look strange referring to num(pclass) as name we add an alias after it (myNumber) and suddenly it is much easier to referens it from the R-script (q$myNumber). The script/function returns one numeric value back to Qlik.


The next four functions allows for having arguments of different data types. Nevertheless, the return type must be either string or numeric. Use cases for these functions can be text analysis and clustering where number of clusters (numeric) is one parameter and the text data as the other (string).

| __Function Name__ | __Function Type__ | __Argument Type__ | __Return Type__ |
| ----- | ----- | ----- | ----- |
| __ScriptEvalEx__ | Scalar, Tensor | Numeric or String | Numeric |
| __ScriptEvalExStr__ | Scalar, Tensor | Numeric or String | String |
| __ScriptAggrEx__ | Aggregation | Numeric or String | Numeric |
| __ScriptAggrExStr__ | Aggregation | Numeric or String | String |

The function call is the same as above except for an additional parameter before the script. 
`<EngineSSEName>.<FunctionName>(ParameterDataTypes, Script [,Parameter...])`
Where 
* `ParameterDataTypes` : String containing the data types of the parameters, ordered according to the given parameters. For example, 'NSN' means we have provided three parameters, the first one is numeric (N), the second one string (S) and the last one is numeric (N).

### Example expression 3
`R.ScriptEvalEx('NS', 'q$pclass+nchar(q$sex);' ,pclass, sex)`
Here we pass two data fields from Qlik (pclass and sex). The first parameter is of type numeric (defined as N in the first function parameter) and the second is of type string (defined as S in the first function parameter). The R-script then referens those data fields through the q dataframe (q$pclass and q$sex). The script/function returns rows of numerics back to Qlik.

### Example expression 4
`R.ScriptAggrExStr('SS', 'paste(q$age_b[1],q$sex[1]);' ,age_b, sex)`
Here we pass two data fields of type string from Qlik (age_b and sex, defined as SS in the first function parameter). The script/function returns one string value back to Qlik.

# Configure SSEtoRserve
In the file `SSEtoRserve.exe.config` you can configure the following: 
* **grpcPort** :  Default `50051` . The gRPC port this plugin should open (the port Qlik Sense is connecting to). Set to another port if you have other SSE plugins already defined on that port.  
* **grpcHost** :  Default `localhost` . The gRPC host this plugin should open the port for. Set to `0.0.0.0` if you want this plugin to be reachable from another machine than this (typically if Qlik is installed on another machine).  
* **rservePort** :  Default `6311` . The Rserve port this plugin should connect to.  
* **rserveHost** :  Default `127.0.0.1` . The Rserve host this plugin should connect to.  
* **certificateFolderFullPath** :  Default empty (insecure connection opened). If you want to enable mutual authentication (server and client auth) between this plugin and Qlik, then define the full path to folder where the certificate files are located, i.e. `C:\sse_qliktest_server_certs` .  
* **rserveInitScript** :  Default empty (no additional action). If set to some R-script, i.e. `library(TTR); library(pastecs);` , then SSEtoRserve will send this initialization R script directly after a connection is created to Rserve. This way you may get rid of unnecessary library loading in every expression within the Qlik apps.  
* **rProcessPathToStart** :  Default empty (no additional action, SSEtoRserve assumes that Rserve is running already). If you want SSEtoRserve to start any R process during startup then set it to i.e. `C:\Program Files\R\R-3.3.3\bin\x64\Rserve.exe` or `C:\Program Files\Microsoft\R Open\bin\x64\Rterm.exe` or `C:\Program Files\R\R-3.3.3\bin\x64\Rterm.exe` , depending on what you want. If the process dies then SSEtoRserve will try to start it again after ~10 seconds.  
* **rProcessCommandLineArgs** :  Default empty (no arguments passed when starting the rProcess defined above). If rProcessPathToStart is defined you can define the arguments here i.e. `--vanilla -e "library(Rserve); Rserve(port = 6311, wait = TRUE);"` if Rterm is started or `--RS-port 6311` if Rserve is started.  
