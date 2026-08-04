// OS-aware path helpers, informed by /api/system/info (see store.js).
import { state } from './store.js';

export const sep = () => state.system?.pathSeparator ?? '/';

export const joinPath = (...parts) => parts.filter(Boolean).join(sep());
