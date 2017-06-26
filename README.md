# SSE R-plugin

Server Side Extension (SSE) is a general technology for extending the Qlik built in expression library, both for load-script and for chart expressions with functionality from external calculation engines. The main purpose is to use SSE in Qlik visualization measures and to some extent also for calculated dimensions.

R is not a supported language in gRPC by default. The purpose of this repository is therefore to provide a possible solution using a C# SSE plugin which in turn access Rserve to be able to run R scripts. All documentation and guides regarding SSE in general, referred to as server-side-extension, are valid and useful for this plugin as well.

## Status
**Current Plugin Version and State:** v1.0.0  
**Matching Qlik Sense Version:** Qlik Sense 2017 release (or later). Both desktop and enterprise.  
**Disclaimer:** This plugin is not meant to be used in production, therefore **no support is provided**. Use it at your own risk.  

[Previous Versions](docs/versions.md)

## Documentation
See the docs folder and the general SSE repo (server-side-extension). 

* [Architecture Overview](docs/overview.md)
* [Communication Flow](docs/communication_flow.md)
* [Generating certificates for secure connection](https://github.com/qlik-oss/server-side-extension/blob/master/generate_certs_guide/README.md) (server-side-extension)
* [Limitations](https://github.com/qlik-oss/server-side-extension/blob/master/docs/limitations.md) (server-side-extension)
* [API Description](https://github.com/qlik-oss/server-side-extension/blob/master/docs/SSE_Protocol.md) (server-side-extension)

## Build and Run the R-Plugin!

* [Get Started](GetStarted.md)

## Secure connection using certificates
Enable secure connection between the plugin server and Qlik by enabling mutual authentication. See the folder `generate_certs_guide` that explains how to generate proper certificates. This can be found in the general SSE repo (server-side-extension). 

## Limitations in this version of SSE
 
#### Load Script (Qlik Sense Limitation)
- The functions that the SSE plugins provide may not show up properly in the script editor in Qlik Sense which means the intellisense may complain about it and show error, even if it works just fine to execute.
- No support for Tensor calls from load script. Only scalar and aggregation.
- Resident Table load only.

#### Returning Data
- There is NO support of returning more rows or a matrix of data back to Qlik Sense. The cardinality of the response from the plugin must be the same as sent from Qlik Sense.

#### Changed Plugin Configuration (Qlik Sense Limitation)
- If you make changes to the plugin config or add/remove plugins you have to restart Qlik Sense Desktop or the Qlik Sense Engine Service in Server version. It is only during Engine startup that the plugin is connected and the GetCapability plugin method is called.

#### QlikView
- This version of SSE is not supported in QlikView yet. We are planning to release SSE support in QlikView during 2017-H2.


## License
See [LICENSE.txt](LICENSE.txt).
Also have a look at [NOTICE.txt](NOTICE.txt).

## Contributing
Please follow the instructions in [CONTRIBUTING.md](.github/CONTRIBUTING.md).
