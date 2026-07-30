// Deterministic username → color. Mirrors UsernameColor.cs exactly (same
// palette, same FNV-1a over the upper-cased name) so server- and
// client-rendered colors always agree.
const PALETTE = [
  '#06B6D4', '#8B5CF6', '#10B981', '#F59E0B',
  '#3B82F6', '#EC4899', '#14B8A6', '#A855F7',
];

export function usernameColor(name) {
  const upper = name.toUpperCase();
  let hash = 2166136261 >>> 0;
  for (let i = 0; i < upper.length; i++) {
    hash = (hash ^ upper.charCodeAt(i)) >>> 0;
    hash = Math.imul(hash, 16777619) >>> 0;
  }
  return PALETTE[hash % PALETTE.length];
}
