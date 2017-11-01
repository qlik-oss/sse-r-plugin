# Communication Flow

The sequence below describes the communication between Qlik Sense Client, Qlik Sense Engine, the SSE Plugin and R  (the communication between _QlikView_ and the SSE plugin works in the same way). The only difference from the communication flow described in the general server-side-extension repository is the communication between the plugin and R (Rserve).

The sequence in words:

1. R Studio (Rserve) is started and opens up the localhost tcp/ip port 6311.
2. The SSE plugin (SSEtoRserve) is started.
    * The given port, e.g. 50051, opens
3. The Engine Service is started.
4. Engine checks the SSE configuration. Are there any plugins configured? What are their names and on what addresses are they running? Are they running with secure or insecure connection?
5. Engine uses the address and the name fetched in the configuration and makes the first call to the plugin using `GetCapabilities`, an RPC method, to fetch the capabilities of the plugin.
6. The plugin answers with its capabilities. Is script evaluation enabled or not? Are any functions added? Does the plugin have an identifier and a version?
7. Engine publishes script functions if script evaluation is enabled and possible plugin functions, to the BNF to enable syntax checking in the expression and load script editors.
8. An object, or a sequence in the load script, in the Sense Client contains an SSE expression.
9. The Sense Client sends a request of GetLayout to the Engine.
10. Engine determines if the SSE call is a script function or a plugin function. If it's the former, the RPC method `EvaluateScript` will be called, otherwise `ExecuteFunction` is called.
11. Either `EvaluateScript` or `ExecuteFunction` is executed in the plugin which is connecting to Rserve and tells it to execute the script with the data located in the data frame named q.
12. Rserve returns the result to the SSE plugin which in turn returns the result values to the Engine.
13. Engine returns the result to the Client.
14. The Client can finish rendering the object(or loading the data in the data script) and the result is visible for the user.
