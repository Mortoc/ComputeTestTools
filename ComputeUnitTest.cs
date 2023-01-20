using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Mortoc.Compute.TestTools
{
    /// <summary>
    /// Mark a method as the setup function for a ComputeShader unit test.
    /// The compute shader used will have the same name as the .cs file, for
    /// example TessellationMeshTest.cs will load TessellationMeshTest.compute.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class ComputeShaderTest : Attribute
    {
        public readonly string File;
        
        public ComputeShaderTest
        (
            [CallerFilePath] string file = ""
        ) {
            File = file;
        }
    }
    
    
    [TestFixture, SingleThreaded]
    public abstract class ComputeUnitTest
    {
        private const int RESULT_BUFFER_SIZE = 1024;
        
        public class ComputeShaderTestFixture : IDisposable
        {
            public ComputeShader Shader;
            public int KernelId;
            internal MethodInfo Method;
            public int3 DispatchSize;
            public string[] ShaderSrcLines;
            public string ShaderFilename;

            public event Action AfterDispatch;
            
            internal void PostDispatch()
            {
                AfterDispatch?.Invoke();
            }
            
            public override string ToString()
            {
                return Method.Name;
            }
            
            public void Dispose()
            {
                if (Shader) 
                {
                    ComputeShader.DestroyImmediate(Shader);
                }
            }
        }

        private readonly List<GraphicsBuffer> _testBuffers = new();
        private readonly List<ComputeShaderTestFixture> _testFixtures = new();
        
        private GraphicsBuffer _passFailBuffer;
        private int2[] _passFailInitialData = new int2[RESULT_BUFFER_SIZE];
        private int2[] _passFailResultData = new int2[RESULT_BUFFER_SIZE];
        
        private GraphicsBuffer _testCaseCounterBuffer;
        private uint[] _testCaseCounterInitialData = { 0u };
        private uint[] _testCaseCounterResultData = { 0u };
        
        /// <summary>
        /// Convenience method to generate a triangle mesh for tests.
        /// Size is 1x1xsqrt(2) with verts at (0,0,0), (1,0,0), (0,1,0)
        /// </summary>
        protected Lazy<Mesh> TriangleMesh { get; private set; }
        
        
        /// <summary>
        /// Convenience method to generate a quad mesh for tests.
        /// Size is 1x1 with verts at (0,0,0), (1,0,0), (0,1,0) and (1,1,0)
        /// </summary>
        protected Lazy<Mesh> QuadMesh { get; private set; }
        
        
        [SetUp]
        public void Setup()
        {
            TriangleMesh = new (BuildTriangleMesh);
            QuadMesh = new (BuildQuadMesh);
            
            _passFailBuffer = GetTestBuffer(
                $"{GetType().Name}.TestResults",
                GraphicsBuffer.Target.Structured,
                RESULT_BUFFER_SIZE,
                sizeof(int) * 2
            );
            for (var i = 0; i < _passFailInitialData.Length; i++)
            {
                _passFailInitialData[i].x = -1;
                _passFailInitialData[i].y = -1;
            }

            _testCaseCounterBuffer = GetTestBuffer(
                $"{GetType().Name}.TestCaseCounter",
                GraphicsBuffer.Target.Structured,
                1,
                sizeof(uint)
            );
        }

        private static ComputeShader InstantiateShaderFromPath(string path)
        {
            var shaderAsset = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
            Assert.IsNotNull(shaderAsset, $"Unable to find compute shader at {path}");
            var shader = ComputeShader.Instantiate(shaderAsset);
            Assert.IsNotNull(shader, $"Unable to instantiate compute shader from {path}");
            return shader;
        }
        
        private string FindScriptUnityFilePath(string path)
        {
            // Find the Unity Asset that corresponds with the given script path
            var filename = Path.GetFileNameWithoutExtension(path);
            
            // Since there would be multiple scripts with the same filename, figure out which
            // of the scripts match the input path
            var possibleMatchGuids = AssetDatabase.FindAssets($"{filename} t:script");
            var resultPath = string.Empty;
            foreach (var guid in possibleMatchGuids)
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                var fullPath = Path.GetFullPath(assetPath);
                if (fullPath == path)
                {
                    resultPath = assetPath;
                }
            }
            
            if (string.IsNullOrEmpty(resultPath))
            {
                throw new Exception($"Unable to find Unity Asset for {path}");
            }
            
            return resultPath;
        }

        [DatapointSource]
        public IEnumerable<ComputeShaderTestFixture> FindComputeShaderTests()
        {
            var bindingFlags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            foreach (var mi in GetType().GetMethods(bindingFlags))
            {
                var computeTestAttrib = mi.CustomAttributes
                    .FirstOrDefault(attribData => attribData.AttributeType == typeof(ComputeShaderTest));
                if (computeTestAttrib != null)
                {
                    var sourceFilePath = computeTestAttrib.ConstructorArguments[0].Value as string;
                    var sourceFilePathUnity = FindScriptUnityFilePath(sourceFilePath);
                    var computeFilePathUnity = Path.ChangeExtension(sourceFilePathUnity, "compute");
                    
                    var computeShader = InstantiateShaderFromPath(computeFilePathUnity);

                    if (!computeShader.HasKernel(mi.Name))
                    {
                        Assert.Fail($"Cannot find Kernel {mi.Name} in {computeFilePathUnity}. (Did the shader compile correctly?)");
                    }
                    var kernelId = computeShader.FindKernel(mi.Name);

                    var fixture = new ComputeShaderTestFixture
                    {
                        Method = mi,
                        Shader = computeShader,
                        KernelId = kernelId,
                        DispatchSize = new int3(1, 1, 1),
                        ShaderSrcLines = File.ReadAllLines(Path.GetFullPath(computeFilePathUnity)),
                        ShaderFilename = Path.GetFileName(computeFilePathUnity)
                    };
                    _testFixtures.Add(fixture);
                    yield return fixture;
                }
            }
        }
        
        [Theory]
        public void RunComputeShaderTests(ComputeShaderTestFixture fixtureData)
        {
            // Clear the test results buffers
            _passFailBuffer.SetData(_passFailInitialData);
            //_testCaseCounterBuffer.SetData(_testCaseCounterInitialData);
            fixtureData.Shader.SetInt("TestCaseCounter", 0);
            
            // Run test setup
            fixtureData.Method.Invoke(this, new object[] {fixtureData});
            
            // Setup test results buffers
            fixtureData.Shader.SetBuffer(fixtureData.KernelId, "PassFail", _passFailBuffer);
            fixtureData.Shader.SetBuffer(fixtureData.KernelId, "TestCaseCounter", _testCaseCounterBuffer);
            
            // Run the compute shader test
            fixtureData.Shader.Dispatch(
                fixtureData.KernelId, 
                fixtureData.DispatchSize.x, 
                fixtureData.DispatchSize.y, 
                fixtureData.DispatchSize.z
            );
            
            // Report test results
            _passFailBuffer.GetData(_passFailResultData);
            int testCases;
            for (testCases = 0; 
                 testCases < _passFailResultData.Length && _passFailResultData[testCases].y >= 0; 
                 ++testCases
            );

            Assert.Greater(testCases, 0, $"No assertions found in the compute shader {fixtureData.Shader}");
            for (var i = 0; i < testCases; ++i)
            {
                if (_passFailResultData[i].y == 0)
                {
                    var shaderSrc = fixtureData.ShaderSrcLines[_passFailResultData[i].x - 1].Trim();
                    Assert.Fail(
                        $"{shaderSrc} failed at {fixtureData.ShaderFilename}:{_passFailResultData[i].x}"
                    );
                }
            }

            fixtureData.PostDispatch();
        }

        [TearDown]
        public void Teardown()
        {
            foreach (var buffer in _testBuffers)
            {
                if (buffer != null)
                {
                    buffer.Dispose();
                }
            }
            
            foreach (var fixture in _testFixtures)
            {
                if (fixture.Shader)
                {
                    ComputeShader.DestroyImmediate(fixture.Shader);
                }
            }
            
            if (TriangleMesh.IsValueCreated)
            {
                Mesh.DestroyImmediate(TriangleMesh.Value);
            }
            
            if (QuadMesh.IsValueCreated)
            {
                Mesh.DestroyImmediate(QuadMesh.Value);
            }
        }

        /// <summary>
        /// Gets a graphics buffer that will be cleaned up at the end of this unit test.
        /// </summary>
        protected GraphicsBuffer GetTestBuffer(string name, GraphicsBuffer.Target target, int count, int stride)
        {
            var buffer = new GraphicsBuffer(target, count, stride);
            buffer.name = name;
            _testBuffers.Add(buffer);
            return buffer;
        }

        private static Mesh BuildTriangleMesh()
        {
            var mesh = new Mesh
            {
                vertices = new[]
                {
                    new Vector3(0.0f, 0.0f, 0.0f),
                    new Vector3(1.0f, 0.0f, 0.0f),
                    new Vector3(0.0f, 1.0f, 0.0f),
                },
                normals = new[]
                {
                    new Vector3(0.0f, 0.0f, 1.0f),
                    new Vector3(0.0f, 0.0f, 1.0f),
                    new Vector3(0.0f, 0.0f, 1.0f),
                },
                uv = new[]
                {
                    new Vector2(0.0f, 0.0f),
                    new Vector2(1.0f, 0.0f),
                    new Vector2(0.0f, 1.0f),
                },
                triangles = new[] {0, 1, 2}
            };
            mesh.UploadMeshData(false);
            return mesh;
        }
        
        private static Mesh BuildQuadMesh()
        {
            var mesh = new Mesh
            {
                vertices = new[]
                {
                    new Vector3(0.0f, 0.0f, 0.0f),
                    new Vector3(1.0f, 0.0f, 0.0f),
                    new Vector3(0.0f, 1.0f, 0.0f),
                    new Vector3(1.0f, 1.0f, 0.0f),
                },
                normals = new[]
                {
                    new Vector3(0.0f, 0.0f, 1.0f),
                    new Vector3(0.0f, 0.0f, 1.0f),
                    new Vector3(0.0f, 0.0f, 1.0f),
                    new Vector3(0.0f, 0.0f, 1.0f),
                },
                uv = new[]
                {
                    new Vector2(0.0f, 0.0f),
                    new Vector2(1.0f, 0.0f),
                    new Vector2(0.0f, 1.0f),
                    new Vector2(1.0f, 1.0f),
                },
                triangles = new[] {0, 1, 2, 2, 1, 3}
            };
            mesh.UploadMeshData(false);
            return mesh;
        }
    }
}
