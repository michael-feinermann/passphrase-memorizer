#if GGML_METAL_EMBED_LIBRARY
    // Deterministic build overlay: compile at build time, load only from
    // executable memory. No runtime source or filesystem fallback exists.
    extern const unsigned char local_ai_default_metallib_start[];
    extern const unsigned char local_ai_default_metallib_end[];
    extern const unsigned char local_ai_tensor_metallib_start[];
    extern const unsigned char local_ai_tensor_metallib_end[];
    NSError * error = nil;
    dispatch_data_t data = dispatch_data_create(local_ai_default_metallib_start,
        local_ai_default_metallib_end - local_ai_default_metallib_start,
        dispatch_get_global_queue(QOS_CLASS_DEFAULT, 0), DISPATCH_DATA_DESTRUCTOR_DEFAULT);
    res->objs[0] = [device newLibraryWithData:data error:&error];
    dispatch_release(data);
    res->single_library = true;
    if (!res->objs[0]) {
        ggml_metal_library_free(res);
        return NULL;
    }
    if (ggml_metal_device_get_props(dev)->has_tensor) {
        extern bool local_ai_tensor_kernels_loaded;
        dispatch_data_t tensor_data = dispatch_data_create(local_ai_tensor_metallib_start,
            local_ai_tensor_metallib_end - local_ai_tensor_metallib_start,
            dispatch_get_global_queue(QOS_CLASS_DEFAULT, 0), DISPATCH_DATA_DESTRUCTOR_DEFAULT);
        id<MTLLibrary> tensor_library = [device newLibraryWithData:tensor_data error:&error];
        dispatch_release(tensor_data);
        if (!tensor_library) {
            ggml_metal_library_free(res);
            return NULL;
        }
        res->objs[GGML_METAL_LIB_MUL_MM] = tensor_library;
        res->override_fns = [[NSSet setWithArray:[tensor_library functionNames]] retain];
        local_ai_tensor_kernels_loaded = true;
    }
    return res;
