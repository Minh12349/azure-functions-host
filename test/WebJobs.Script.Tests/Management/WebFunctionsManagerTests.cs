﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Script.Description;
using Microsoft.Azure.WebJobs.Script.WebHost.Extensions;
using Microsoft.Azure.WebJobs.Script.WebHost.Management;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Microsoft.Azure.WebJobs.Script.Tests.Managment
{
    public class WebFunctionsManagerTests : IDisposable
    {
        private readonly string _testRootScriptPath;
        private readonly string _testHostConfigFilePath;
        private readonly ScriptHostConfiguration _hostConfig;

        public WebFunctionsManagerTests()
        {
            _testRootScriptPath = Path.GetTempPath();
            _testHostConfigFilePath = Path.Combine(_testRootScriptPath, ScriptConstants.HostMetadataFileName);
            FileUtility.DeleteFileSafe(_testHostConfigFilePath);

            _hostConfig = new ScriptHostConfiguration
            {
                RootScriptPath = @"x:\root\site\wwwroot",
                IsSelfHost = false,
                RootLogPath = @"x:\root\LogFiles\Application\Functions",
                TestDataPath = @"x:\root\data\functions\sampledata"
            };
            _hostConfig.HostConfig.HostId = "testhostid123";
        }

        [Fact]
        public async Task ReadFunctionsMetadataSucceeds()
        {
            // Setup
            var fileSystem = CreateFileSystem(_hostConfig);
            var loggerProvider = new TestLoggerProvider();
            var loggerFactory = new LoggerFactory();
            loggerFactory.AddProvider(loggerProvider);

            var contentBuilder = new StringBuilder();
            var httpClient = CreateHttpClient(contentBuilder);
            var webManager = new WebFunctionsManager(_hostConfig, loggerFactory);

            FileUtility.Instance = fileSystem;
            var functions = await webManager.GetFunctionsMetadata();
            var jsFunctions = functions.Where(funcMetadata => funcMetadata.Language == ScriptType.Javascript.ToString()).ToList();
            var pytonFunctions = functions.Where(funcMetadata => funcMetadata.Language == ScriptType.Python.ToString()).ToList();

            Assert.Equal(3, functions.Count());
            Assert.Equal(2, jsFunctions.Count());
            Assert.Equal(1, pytonFunctions.Count());
        }

        [Theory]
        [InlineData(null, "api")]
        [InlineData("", "api")]
        [InlineData("this { not json", "api")]
        [InlineData("{}", "api")]
        [InlineData("{ extensions: {} }", "api")]
        [InlineData("{ extensions: { http: {} }", "api")]
        [InlineData("{ extensions: { http: { routePrefix: 'test' }, foo: {} } }", "test")]
        public void GetRoutePrefix_Succeeds(string content, string expected)
        {
            if (content != null)
            {
                File.WriteAllText(_testHostConfigFilePath, content);
            }

            string prefix = WebFunctionsManager.GetRoutePrefix(_testRootScriptPath);
            Assert.Equal(expected, prefix);
        }

        [Fact]
        public void GetFunctionInvokeUrlTemplate_ReturnsExpectedResult()
        {
            string baseUrl = "https://localhost";
            var functionMetadata = new FunctionMetadata
            {
                Name = "TestFunction"
            };
            var httpTriggerBinding = new BindingMetadata
            {
                Name = "req",
                Type = "httpTrigger",
                Direction = BindingDirection.In,
                Raw = new JObject()
            };
            functionMetadata.Bindings.Add(httpTriggerBinding);
            var uri = FunctionMetadataExtensions.GetFunctionInvokeUrlTemplate(baseUrl, functionMetadata, "api");
            Assert.Equal("https://localhost/api/testfunction", uri.ToString());

            // with empty route prefix
            uri = FunctionMetadataExtensions.GetFunctionInvokeUrlTemplate(baseUrl, functionMetadata, string.Empty);
            Assert.Equal("https://localhost/testfunction", uri.ToString());

            // with a custom route
            httpTriggerBinding.Raw.Add("route", "catalog/products/{category:alpha?}/{id:int?}");
            uri = FunctionMetadataExtensions.GetFunctionInvokeUrlTemplate(baseUrl, functionMetadata, "api");
            Assert.Equal("https://localhost/api/catalog/products/{category:alpha?}/{id:int?}", uri.ToString());

            // with empty route prefix
            uri = FunctionMetadataExtensions.GetFunctionInvokeUrlTemplate(baseUrl, functionMetadata, string.Empty);
            Assert.Equal("https://localhost/catalog/products/{category:alpha?}/{id:int?}", uri.ToString());
        }

        [Theory]
        [InlineData("testapp.azurewebsites.net", "https://testapp.scm.azurewebsites.net")]
        [InlineData("testapp.testase.p.azurewebsites.net", "https://testapp.scm.testase.p.azurewebsites.net")]
        [InlineData("testapp.2gdfrew435476jfg", "https://testapp.scm.2gdfrew435476jfg")]
        [InlineData("testapp.chinacloud.cn", "https://testapp.scm.chinacloud.cn")]
        [InlineData("testapp.azurewebsites.us", "https://testapp.scm.azurewebsites.us")]
        [InlineData("testapp", "https://testapp.scm")]
        [InlineData(null, "https://localhost")]
        [InlineData("", "https://localhost")]
        public void GetScmBaseUrl_ReturnsExpectedValue(string hostName, string expected)
        {
            var vars = new Dictionary<string, string>
            {
                { EnvironmentSettingNames.AzureWebsiteHostName, hostName }
            };
            using (var env = new TestScopedEnvironmentVariable(vars))
            {
                Assert.Equal(expected, FunctionMetadataExtensions.GetScmBaseUrl());
            }
        }

        [Fact]
        public void GetAppBaseUrl_ReturnsExpectedValue()
        {
            Assert.Equal("https://localhost", FunctionMetadataExtensions.GetAppBaseUrl());

            var vars = new Dictionary<string, string>
            {
                { EnvironmentSettingNames.AzureWebsiteHostName, "testapp.azurewebsites.net" }
            };
            using (var env = new TestScopedEnvironmentVariable(vars))
            {
                Assert.Equal("https://testapp.azurewebsites.net", FunctionMetadataExtensions.GetAppBaseUrl());
            }
        }

        private static HttpClient CreateHttpClient(StringBuilder writeContent)
        {
            return new HttpClient(new MockHttpHandler(writeContent));
        }

        private static IFileSystem CreateFileSystem(ScriptHostConfiguration hostConfig)
        {
            string rootScriptPath = hostConfig.RootScriptPath;
            string testDataPath = hostConfig.TestDataPath;

            var fullFileSystem = new FileSystem();
            var fileSystem = new Mock<IFileSystem>();
            var fileBase = new Mock<FileBase>();
            var dirBase = new Mock<DirectoryBase>();

            fileSystem.SetupGet(f => f.Path).Returns(fullFileSystem.Path);
            fileSystem.SetupGet(f => f.File).Returns(fileBase.Object);
            fileBase.Setup(f => f.Exists(Path.Combine(rootScriptPath, "host.json"))).Returns(true);

            var hostJson = new MemoryStream(Encoding.UTF8.GetBytes(@"{ ""durableTask"": { ""HubName"": ""TestHubValue"", ""azureStorageConnectionStringName"": ""DurableStorage"" }}"));
            hostJson.Position = 0;
            fileBase.Setup(f => f.Open(Path.Combine(rootScriptPath, @"host.json"), It.IsAny<FileMode>(), It.IsAny<FileAccess>(), It.IsAny<FileShare>())).Returns(hostJson);

            fileSystem.SetupGet(f => f.Directory).Returns(dirBase.Object);

            dirBase.Setup(d => d.EnumerateDirectories(rootScriptPath))
                .Returns(new[]
                {
                    Path.Combine(rootScriptPath, "function1"),
                    Path.Combine(rootScriptPath, "function2"),
                    Path.Combine(rootScriptPath, "function3")
                });

            var function1 = @"{
  ""scriptFile"": ""main.py"",
  ""disabled"": false,
  ""bindings"": [
    {
      ""authLevel"": ""anonymous"",
      ""type"": ""httpTrigger"",
      ""direction"": ""in"",
      ""name"": ""req""
    },
    {
      ""type"": ""http"",
      ""direction"": ""out"",
      ""name"": ""$return""
    }
  ]
}";
            var function2 = @"{
  ""disabled"": false,
  ""scriptFile"": ""main.js"",
  ""bindings"": [
    {
      ""name"": ""myQueueItem"",
      ""type"": ""orchestrationTrigger"",
      ""direction"": ""in"",
      ""queueName"": ""myqueue-items"",
      ""connection"": """"
    }
  ]
}";

            var function3 = @"{
  ""disabled"": false,
  ""scriptFile"": ""main.js"",
  ""bindings"": [
    {
      ""name"": ""myQueueItem"",
      ""type"": ""activityTrigger"",
      ""direction"": ""in"",
      ""queueName"": ""myqueue-items"",
      ""connection"": """"
    }
  ]
}";

            fileBase.Setup(f => f.Exists(Path.Combine(rootScriptPath, @"function1\function.json"))).Returns(true);
            fileBase.Setup(f => f.Exists(Path.Combine(rootScriptPath, @"function1\main.py"))).Returns(true);
            fileBase.Setup(f => f.ReadAllText(Path.Combine(rootScriptPath, @"function1\function.json"))).Returns(function1);
            fileBase.Setup(f => f.Open(Path.Combine(rootScriptPath, @"function1\function.json"), It.IsAny<FileMode>(), It.IsAny<FileAccess>(), It.IsAny<FileShare>())).Returns(() =>
            {
                return new MemoryStream(Encoding.UTF8.GetBytes(function1));
            });
            fileBase.Setup(f => f.Open(Path.Combine(testDataPath, "function1.dat"), It.IsAny<FileMode>(), It.IsAny<FileAccess>(), It.IsAny<FileShare>())).Returns(() =>
            {
                return new MemoryStream(Encoding.UTF8.GetBytes(function1));
            });

            fileBase.Setup(f => f.Exists(Path.Combine(rootScriptPath, @"function2\function.json"))).Returns(true);
            fileBase.Setup(f => f.Exists(Path.Combine(rootScriptPath, @"function2\main.js"))).Returns(true);
            fileBase.Setup(f => f.ReadAllText(Path.Combine(rootScriptPath, @"function2\function.json"))).Returns(function2);
            fileBase.Setup(f => f.Open(Path.Combine(rootScriptPath, @"function2\function.json"), It.IsAny<FileMode>(), It.IsAny<FileAccess>(), It.IsAny<FileShare>())).Returns(() =>
            {
                return new MemoryStream(Encoding.UTF8.GetBytes(function2));
            });
            fileBase.Setup(f => f.Open(Path.Combine(testDataPath, "function2.dat"), It.IsAny<FileMode>(), It.IsAny<FileAccess>(), It.IsAny<FileShare>())).Returns(() =>
            {
                return new MemoryStream(Encoding.UTF8.GetBytes(function1));
            });

            fileBase.Setup(f => f.Exists(Path.Combine(rootScriptPath, @"function3\function.json"))).Returns(true);
            fileBase.Setup(f => f.Exists(Path.Combine(rootScriptPath, @"function3\main.js"))).Returns(true);
            fileBase.Setup(f => f.ReadAllText(Path.Combine(rootScriptPath, @"function3\function.json"))).Returns(function3);
            fileBase.Setup(f => f.Open(Path.Combine(rootScriptPath, @"function3\function.json"), It.IsAny<FileMode>(), It.IsAny<FileAccess>(), It.IsAny<FileShare>())).Returns(() =>
            {
                return new MemoryStream(Encoding.UTF8.GetBytes(function3));
            });
            fileBase.Setup(f => f.Open(Path.Combine(testDataPath, "function3.dat"), It.IsAny<FileMode>(), It.IsAny<FileAccess>(), It.IsAny<FileShare>())).Returns(() =>
            {
                return new MemoryStream(Encoding.UTF8.GetBytes(function1));
            });

            return fileSystem.Object;
        }

        public void Dispose()
        {
            // Clean up mock IFileSystem
            FileUtility.Instance = null;
            Environment.SetEnvironmentVariable(EnvironmentSettingNames.WebsiteAuthEncryptionKey, string.Empty);
            Environment.SetEnvironmentVariable(EnvironmentSettingNames.AzureWebsiteName, string.Empty);
            FileUtility.DeleteFileSafe(_testHostConfigFilePath);
        }

        private class MockHttpHandler : HttpClientHandler
        {
            private StringBuilder _content;

            public MockHttpHandler(StringBuilder content)
            {
                _content = content;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                _content.Append(await request.Content.ReadAsStringAsync());
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            }
        }
    }
}