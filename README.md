# Compute Test Tools

Unit test helper for Compute Shaders


Usage:

### TestSomeShader.compute
File name must match the C# test file
```c++
#ifndef MY_SHADER_TEST_COMPUTE
#define MY_SHADER_TEST_COMPUTE

// Include the ASSERT macro
#include "Packages/com.mortoc.compute.test/ComputeAssert.hlsl"

// Include the shader code to test
#include "MyShaderCodeToTest.hlsl"

StructuredBuffer<uint> SomeBuffer;

// The kernel name needs to match the name of the unit test
// on the C# side
#pragma kernel TestSomeFeatureOfComputeShader
[numthreads(1,1,1)]
void TestSomeFeatureOfComputeShader (uint3 id : SV_DispatchThreadID)
{
    // Arrange
    uint index = MyShaderCodeToTest();
    uint result = SomeBuffer[index];
    
    // Test
    ASSERT(index >= 0 && index < 32);   
    ASSERT(result == 7);
}

#endif
```


### TestSomeShader.cs
File name must match the compute test file
```csharp
public class TestSomeShader : ComputeUnitTest
{

    // ComputeShaderTest attribute signals this is a setup function for a
    // unit test in the corresponding compute shader test.
    [ComputeShaderTest] 
    public void TestUpdateSubDBufferIncrementsCounter(ComputeShaderTestFixture fixture)
    {
        // The C# code here is run before the compute shader for any setup code.
        var testBuffer = GetTestBuffer(
            $"{nameof(TestSomeShader)}.SomeBuffer",
            GraphicsBuffer.Target.Structured,
            32,
            sizeof(uint)
        );
        fixture.Shader.SetBuffer(fixture.KernelId, "SomeBuffer", testBuffer);
        
        // Normal C# assertions can be done at this stage as well
        Assert.IsTrue(fixture.Shader.IsSupported());
        
        // Assertions to be run after the compute shader can be added to the 
        // AfterDispatch event
        fixture.AfterDispatch += () => 
        {
            var testResultData = new uint[32];
            testBuffer.GetData(testResultData);
            
            Assert.AreEqual(7, testResultData[0]);    
        };
    }
}
```