#!/usr/bin/env python3
"""Adapt Mesa 24.3.4's existing LLVM bindless sampler implementation for Snooper.

OSMesa only: expose CPU descriptor pointers to OpenGL instead of lavapipe's
descriptor-set/offset pairs. No paging is needed for resident CPU allocations.
The pinned source and exact replacements keep upstream changes visible.
"""
from pathlib import Path
import sys

root = Path(sys.argv[1])

def replace(path, before, after):
    file = root / path
    source = file.read_text()
    if after in source:
        return
    if source.count(before) != 1:
        raise SystemExit(f"Unexpected Mesa source: {path}")
    file.write_text(source.replace(before, after))

lp = "src/gallium/drivers/llvmpipe/"
replace(lp + "lp_screen.c", "   case PIPE_CAP_SYSTEM_SVM:\n", "   case PIPE_CAP_BINDLESS_TEXTURE:\n   case PIPE_CAP_SYSTEM_SVM:\n")
replace(lp + "lp_texture_handle.c", '#include "lp_context.h"', '#include "util/u_inlines.h"\n#include "lp_context.h"')
replace(lp + "lp_texture_handle.c", "void\nllvmpipe_init_sampler_matrix(struct llvmpipe_context *ctx)", r"""
/* Snooper's immutable GL texture storage remains addressable for the lifetime
 * of each handle. Keep its view alive until GL destroys the handle. */
struct lp_gl_texture_handle {
   struct lp_descriptor descriptor;
   struct pipe_sampler_view *view;
};

static uint64_t
llvmpipe_create_gl_texture_handle(struct pipe_context *ctx,
                                struct pipe_sampler_view *view,
                                const struct pipe_sampler_state *sampler)
{
   struct lp_texture_handle *internal = (void *)(uintptr_t)
      llvmpipe_create_texture_handle(ctx, view, sampler);
   struct lp_gl_texture_handle *gl = calloc(1, sizeof(*gl));
   if (!gl) { free(internal); return 0; }
   pipe_sampler_view_reference(&gl->view, view);
   lp_jit_texture_from_pipe(&gl->descriptor.texture, view);
   lp_jit_sampler_from_pipe(&gl->descriptor.sampler, sampler);
   gl->descriptor.functions = internal->functions;
   gl->descriptor.texture.sampler_index = internal->sampler_index;
   free(internal);
   return (uint64_t)(uintptr_t)gl;
}

static void
llvmpipe_delete_gl_texture_handle(struct pipe_context *ctx, uint64_t handle)
{
   struct lp_gl_texture_handle *gl = (void *)(uintptr_t)handle;
   pipe_sampler_view_reference(&gl->view, NULL);
   free(gl);
}

static void
llvmpipe_gl_texture_residency(struct pipe_context *ctx, uint64_t handle, bool resident)
{
   /* All referenced software resources are already resident in CPU memory. */
}

void
llvmpipe_init_sampler_matrix(struct llvmpipe_context *ctx)""")
replace(lp + "lp_texture_handle.c",
        "   ctx->pipe.create_texture_handle = llvmpipe_create_texture_handle;\n   ctx->pipe.delete_texture_handle = llvmpipe_delete_texture_handle;",
        "   ctx->pipe.create_texture_handle = llvmpipe_create_gl_texture_handle;\n   ctx->pipe.delete_texture_handle = llvmpipe_delete_gl_texture_handle;\n   ctx->pipe.make_texture_handle_resident = llvmpipe_gl_texture_residency;")

replace("src/gallium/auxiliary/gallivm/lp_bld_nir_soa.c",
        '   LLVMValueRef set = LLVMBuildExtractValue(gallivm->builder, resource, 0, "");',
        '''   if (LLVMGetTypeKind(LLVMTypeOf(resource)) == LLVMVectorTypeKind)
      return LLVMBuildExtractElement(gallivm->builder, resource, invocation, "");

   LLVMValueRef set = LLVMBuildExtractValue(gallivm->builder, resource, 0, "");''')
replace("src/gallium/auxiliary/gallivm/lp_bld_jit_types.c",
        '   LLVMValueRef desc_set_index = LLVMBuildExtractValue(builder, index, 0, "");',
        '''   if (LLVMGetTypeKind(LLVMTypeOf(index)) == LLVMIntegerTypeKind)
      return index; /* OpenGL bindless handle: absolute CPU descriptor. */

   LLVMValueRef desc_set_index = LLVMBuildExtractValue(builder, index, 0, "");''')
