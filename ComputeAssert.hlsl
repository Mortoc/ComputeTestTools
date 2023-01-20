#ifndef MORTOC_COMPUTE_ASSERT_HLSL
#define MORTOC_COMPUTE_ASSERT_HLSL

// PassFail buffer is initialized to (-1, -1) by the Unit test.
// When a test runs, the line number is written into x and the
// pass/fail bit is set into y.
RWStructuredBuffer<int2> PassFail;
groupshared uint TestCaseCounter = 0;

#define ASSERT(condition) { \
    uint testCase; InterlockedAdd(TestCaseCounter, 1u, testCase); \
    PassFail[testCase] = int2(__LINE__, (int)(condition)); \
}

#endif