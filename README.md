# SSE R-plugin

This repository provides a server-side extension (SSE) that allows you to extend the Qlik built-in expression library with functionality from R. You can use this in both load scripts and charts. In Qlik Sense and QlikView, you connect to this SSE R-plugin by defining an analytic connection.

R is not a supported language in gRPC by default. The purpose of this repository is therefore to provide an SSE, written in C# and is called SSEtoRserve, that accesses Rserve to be able to run R scripts. All documentation and guides regarding SSE in general, referred to as server-side-extension, are valid and useful for this plugin as well.  

## Status
**Latest Version:** [v1.2.1](https://github.com/qlik-oss/sse-r-plugin/releases/latest) (using SSE protocol version v1.1.0)    
**Disclaimer:** Use it at your own risk. See [License](#license).  

[All Versions](docs/versions.md)

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

## Limitations

#### Changed Plugin Configuration (Qlik Limitation)
- If you make changes to the plugin config or add/remove plugins you have to restart the Qlik engine (i.e. restarting the Desktop process for Qlik Sense Desktop and QlikView Desktop, and restarting the Qlik Sense Engine Service or QlikView Server Service for server version). It is only during Engine startup that Qlik connects to the plugin and calls the GetCapability plugin method to see what the plugin is capable of and which functions that are available.

## License
See [LICENSE.txt](LICENSE.txt).
Also have a look at [NOTICE.txt](NOTICE.txt).

## Contributing
Please follow the instructions in [CONTRIBUTING.md](.github/CONTRIBUTING.md).
