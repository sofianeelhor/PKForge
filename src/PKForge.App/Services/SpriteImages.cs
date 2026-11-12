using System.Runtime.CompilerServices;
using SkiaSharp;

namespace PKForge.App.Services;

/// <summary>
/// The drawable image of a decoded sprite, made once and kept for as long as the sprite
/// lives. Making it copies every pixel of the bitmap; the box grid did that for each
/// Pokémon on each frame, and for a Showdown icon the bitmap is the whole icon sheet
/// (480 x 4110, eight megabytes): a full box copied a quarter of a gigabyte to draw one
/// frame, which is what made turning the pages of the PC drag.
/// </summary>
internal static class SpriteImages
{
    private static readonly ConditionalWeakTable<SKBitmap, SKImage> Images = new();

    /// <summary>The image of <paramref name="bitmap"/>. It belongs to the cache: never dispose it.</summary>
    public static SKImage Of(SKBitmap bitmap) => Images.GetValue(bitmap, static source => SKImage.FromBitmap(source));
}
