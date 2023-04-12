#!/bin/bash
dotnet publish gpt.csproj -c Release -r linux-x64 -o ~/bin/ --self-contained true -p:PublishSingleFile=true