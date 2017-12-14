@echo off
IF %1.==. GOTO NoArg
cd %1
:NoArg
@echo on
set TOOLS_DIR="..\packages\Grpc.Tools.1.7.3\tools\windows_x64"
set PROTO_DIR="..\..\proto"
%TOOLS_DIR%\protoc -I%PROTO_DIR% --grpc_out=. --csharp_out=. --plugin=protoc-gen-grpc=%TOOLS_DIR%\grpc_csharp_plugin.exe %PROTO_DIR%\ServerSideExtension.proto
exit /b %ERRORLEVEL%