// The original CUE4Parse ABI, backed by the upstream portable CRN decoder.
#include <cstddef>
#include <cstdlib>
#include <cstring>
#include "crn_decomp.h"
extern "C" {
void* crnd_unpack_begin(const void* source, unsigned size) {
    return crnd::crnd_unpack_begin(source, size);
}
int crnd_unpack_level_segmented(void* context, const void* source, unsigned sourceSize,
    void** destination, unsigned destinationSize, unsigned rowPitch, unsigned level) {
    return crnd::crnd_unpack_level_segmented(context, source, sourceSize, destination, destinationSize, rowPitch, level);
}
int crnd_unpack_end(void* context) { return crnd::crnd_unpack_end(context); }
}
