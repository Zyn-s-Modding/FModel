extern crate tegra_swizzle;
use tegra_swizzle::{BlockHeight, swizzle};

// Preserve CUE4Parse's original 64-bit ABI. Return errors to managed code rather
// than allowing a malformed texture to unwind through a native callback.
#[no_mangle]
pub unsafe extern "C" fn deswizzle_block_linear(w: u64, h: u64, d: u64,
    source: *const u8, source_len: u64, destination: *mut u8, destination_len: u64,
    block_height: u64, bpp: u64) -> i32 {
    let result = std::panic::catch_unwind(|| {
        let w = u32::try_from(w).ok()?; let h = u32::try_from(h).ok()?;
        let d = u32::try_from(d).ok()?; let bpp = u32::try_from(bpp).ok()?;
        if w == 0 || h == 0 || d == 0 || bpp == 0 || source.is_null() || destination.is_null() { return None; }
        let block = BlockHeight::new(u32::try_from(block_height).ok()?)?;
        let size = (w as usize).checked_mul(h as usize)?.checked_mul(d as usize)?.checked_mul(bpp as usize)?;
        if size > destination_len as usize { return None; }
        let input = std::slice::from_raw_parts(source, source_len.try_into().ok()?);
        let decoded = swizzle::deswizzle_block_linear(w, h, d, input, block, bpp).ok()?;
        std::ptr::copy_nonoverlapping(decoded.as_ptr(), destination, decoded.len());
        Some(())
    });
    if matches!(result, Ok(Some(()))) { 1 } else { 0 }
}

#[no_mangle]
pub extern "C" fn block_height_mip0(height: u64) -> u64 {
    tegra_swizzle::block_height_mip0(height as u32) as u64
}
#[no_mangle]
pub extern "C" fn mip_block_height(height: u64, base: u64) -> u64 {
    BlockHeight::new(base as u32).map(|b| tegra_swizzle::mip_block_height(height as u32, b) as u64).unwrap_or(0)
}
#[no_mangle]
pub extern "C" fn swizzled_surface_size(w: u64, h: u64, d: u64, block: u64, bpp: u64) -> u64 {
    BlockHeight::new(block as u32).map(|b| swizzle::swizzled_mip_size(w as u32, h as u32, d as u32, b, bpp as u32) as u64).unwrap_or(0)
}
