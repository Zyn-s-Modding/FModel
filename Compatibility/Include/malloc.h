#pragma once
// Linux allocation introspection used by the portable Crunch header.
#include <malloc/malloc.h>
static inline size_t malloc_usable_size(const void* pointer) { return malloc_size(pointer); }
