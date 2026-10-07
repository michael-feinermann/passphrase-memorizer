bool local_ai_tensor_kernels_loaded = false;

static ggml_metal_library_t local_ai_tensor_probe_from_data(ggml_metal_device_t dev, bool bfloat) {
    extern const unsigned char local_ai_f16_probe_start[], local_ai_f16_probe_end[];
    extern const unsigned char local_ai_bf16_probe_start[], local_ai_bf16_probe_end[];
    const unsigned char * begin = bfloat ? local_ai_bf16_probe_start : local_ai_f16_probe_start;
    const unsigned char * end = bfloat ? local_ai_bf16_probe_end : local_ai_f16_probe_end;
    ggml_metal_library_t res = calloc(1, sizeof(struct ggml_metal_library));
    res->dev = dev;
    res->pipelines = ggml_metal_pipelines_init();
    res->lock = [NSLock new];
    res->single_library = true;
    NSError * error = nil;
    dispatch_data_t data = dispatch_data_create(begin, end - begin,
        dispatch_get_global_queue(QOS_CLASS_DEFAULT, 0), DISPATCH_DATA_DESTRUCTOR_DEFAULT);
    id<MTLDevice> device = ggml_metal_device_get_obj(dev);
    res->objs[0] = [device newLibraryWithData:data error:&error];
    dispatch_release(data);
    if (!res->objs[0]) { ggml_metal_library_free(res); return NULL; }
    return res;
}
