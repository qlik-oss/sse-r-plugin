# SSE R-plugin

Server Side Extension (SSE) is a general technology for extending the Qlik built in expression library, both for load-script and for chart expressions with functionality from external calculation engines. The main purpose is to use SSE in Qlik visualization measures and to some extent also for calculated dimensions.  

SSE is part of the Advanced Analytics Integration (AAI) concept at Qlik.  

R is not a supported language in gRPC by default. The purpose of this repository is therefore to provide a possible solution using a C# SSE plugin which in turn accesses Rserve to be able to run R scripts. All documentation and guides regarding SSE in general, referred to as server-side-extension, are valid and useful for this plugin as well.  

## Status
**Current Plugin Version and State:** v1.0.0 (using SSE version v1.0.0)  
**Matching Qlik Sense Version:** Qlik Sense June 2017 release (or later). Both desktop and enterprise.  
**Matching QlikView Version:** QlikView November 2017 (or later). Both desktop and server.  
**Disclaimer:** Use it at your own risk. See [License](#license).  

[Previous Versions](docs/versions.md)

## Documentation
See the docs folder and the general SSE repo (server-side-extension).

* [Overview](docs/README.md)
* [Communication Flow](docs/communication_flow.md)
* [Generating certificates for secure connection](https://github.com/qlik-oss/server-side-extension/blob/master/generate_certs_guide/README.md) (server-side-extension)
* [Limitations](https://github.com/qlik-oss/server-side-extension/blob/master/docs/limitations.md) (server-side-extension)
* [API Description](https://github.com/qlik-oss/server-side-extension/blob/master/docs/SSE_Protocol.md) (server-side-extension)

## Build and Run the R-Plugin!

* [Get Started](GetStarted.md)

## Security

#### General Security Attention 
Since R scripts can be very powerful and you will never know what R script will be executed (when EvaluateScript method is called from the client) by this plugin and the R installation, you must be extra careful to secure the machine that this plugin and the R installation are deployed to as much as you can. If possible, sandbox the execution. Be aware of which user account that is starting the plugin and R installation and what access rights this user got in the machine and in your domain to minimize any harm a malicious script can cause.  

To be in full control, the EvaluateScript functionality can be disabled by setting the config param allowScript to False in the SSEtoRserve config file. See [Configure SSEtoRserve](GetStarted.md#configure-ssetorserve) for more info.  
Instead you can define the functions that should be available in this SSEtoRserve plugin together with predefined R scripts that cannot be changed by the client (Qlik). See [Defining and using your own functions](GetStarted.md#defining-and-using-your-own-functions) for more info.  

#### Secure connection using certificates
Enable secure connection between the plugin server and Qlik by enabling mutual authentication. See the folder `generate_certs_guide` that explains how to generate proper certificates. This can be found in the general SSE repo (server-side-extension).

## Limitations in this version of SSE

#### Load Script (Qlik Limitation)
- No support for Tensor calls from load script. Only scalar and aggregation.
- Resident Table load only.

#### Returning Data
- There is NO support of returning more rows or a matrix of data back to Qlik. The cardinality of the response from the plugin must be the same as sent from Qlik.

#### Changed Plugin Configuration (Qlik Limitation)
- If you make changes to the plugin config or add/remove plugins you have to restart the Qlik engine (i.e. restarting the Desktop process for Qlik Sense Desktop and QlikView Desktop, and restarting the Qlik Sense Engine Service or QlikView Server Service for server version). It is only during Engine startup that the plugin is connected and the GetCapability plugin method is called.

## License
See [LICENSE.txt](LICENSE.txt).
Also have a look at [NOTICE.txt](NOTICE.txt).

## Contributing
Please follow the instructions in [CONTRIBUTING.md](.github/CONTRIBUTING.md).
