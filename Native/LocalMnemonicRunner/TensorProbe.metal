// Build-time capability probe, following llama.cpp's MIT-licensed tensor probe.
#include <metal_stdlib>
#include <metal_tensor>
#include <MetalPerformancePrimitives/MetalPerformancePrimitives.h>
using namespace metal;
using namespace mpp::tensor_ops;
#if LOCAL_AI_PROBE_BF16
using element = bfloat;
#else
using element = half;
#endif
kernel void dummy_kernel(
    tensor<device element, dextents<int32_t, 2>> A [[buffer(0)]],
    tensor<device element, dextents<int32_t, 2>> B [[buffer(1)]],
    device float * C [[buffer(2)]], uint2 group [[threadgroup_position_in_grid]]) {
    auto first = A.slice(0, (int)group.y);
    auto second = B.slice((int)group.x, 0);
    matmul2d<matmul2d_descriptor(16, 16, dynamic_extent), execution_simdgroups<4>> multiply;
    auto destination = multiply.get_destination_cooperative_tensor<decltype(first), decltype(second), float>();
    auto left = first.slice(0, 0);
    auto right = second.slice(0, 0);
    multiply.run(right, left, destination);
    auto output = tensor<device float, dextents<int32_t, 2>, tensor_inline>(C, dextents<int32_t, 2>(16, 16));
    destination.store(output);
}
