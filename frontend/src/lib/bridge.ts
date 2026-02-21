type Listener<T = unknown> = (event: { payload: T }) => void;

type HostEventMessage = {
  type: 'event';
  event: string;
  payload?: unknown;
};

type HostResponseMessage = {
  type: 'response';
  id: number;
  result?: unknown;
  error?: string;
};

type PendingRequest = {
  resolve: (value: unknown) => void;
  reject: (reason?: unknown) => void;
};

type TauriBridge = {
  core?: {
    invoke?: (command: string, args?: Record<string, unknown>) => Promise<unknown>;
  };
  event?: {
    listen?: (event: string, callback: (payload: { payload: unknown }) => void) => Promise<() => void>;
  };
  dialog?: {
    open?: (options?: {
      multiple?: boolean;
      filters?: Array<{ name: string; extensions: string[] }>;
    }) => Promise<string | string[] | null>;
  };
};

type TauriCoreModule = {
  invoke: <T = unknown>(command: string, args?: Record<string, unknown>) => Promise<T>;
};

type TauriEventModule = {
  listen: <T = unknown>(event: string, handler: (event: { payload: T }) => void) => Promise<() => void>;
};

type TauriDialogModule = {
  open: (options?: {
    multiple?: boolean;
    filters?: Array<{ name: string; extensions: string[] }>;
  }) => Promise<string | string[] | null>;
};

const listeners = new Map<string, Set<Listener>>();
const pending = new Map<number, PendingRequest>();
let requestId = 0;
let receiverInitialized = false;
const tauriUnlisten = new Map<string, () => void>();
let tauriCoreModule: Promise<TauriCoreModule> | null = null;
let tauriEventModule: Promise<TauriEventModule> | null = null;
let tauriDialogModule: Promise<TauriDialogModule> | null = null;

type RuntimeKind = 'tauri' | 'webview2' | 'none';

function isBridgeDebugEnabled(): boolean {
  const win = window as unknown as { __LINGOFIX_BRIDGE_DEBUG__?: boolean };
  if (typeof win.__LINGOFIX_BRIDGE_DEBUG__ === 'boolean') {
    return win.__LINGOFIX_BRIDGE_DEBUG__;
  }

  try {
    return window.localStorage.getItem('lingofix.bridge.debug') === '1';
  } catch {
    return false;
  }
}

function bridgeLog(message: string) {
  if (!isBridgeDebugEnabled()) {
    return;
  }

  console.info(`[bridge] ${message}`);
}

function getExternalBridge(): {
  sendMessage?: (message: string) => void;
  receiveMessage?: (handler: (message: string) => void) => void;
} {
  return ((window as unknown as { external?: unknown }).external as {
    sendMessage?: (message: string) => void;
    receiveMessage?: (handler: (message: string) => void) => void;
  }) ?? {};
}

function getTauriBridge(): TauriBridge | null {
  const win = window as unknown as {
    __TAURI__?: TauriBridge;
  };
  return win.__TAURI__ ?? null;
}

function hasWebView2Bridge(): boolean {
  return typeof getExternalBridge().sendMessage === 'function';
}

function detectRuntime(): RuntimeKind {
  if (getTauriBridge()?.core?.invoke) {
    return 'tauri';
  }

  const win = window as unknown as { __TAURI_INTERNALS__?: { invoke?: unknown } };
  if (typeof win.__TAURI_INTERNALS__?.invoke === 'function') {
    return 'tauri';
  }

  if (hasWebView2Bridge()) {
    return 'webview2';
  }

  return 'none';
}

async function getTauriCoreInvoke(): Promise<TauriCoreModule['invoke'] | null> {
  const globalInvoke = getTauriBridge()?.core?.invoke;
  if (globalInvoke) {
    return globalInvoke as TauriCoreModule['invoke'];
  }

  try {
    tauriCoreModule ??= import('@tauri-apps/api/core') as Promise<TauriCoreModule>;
    const mod = await tauriCoreModule;
    return mod.invoke;
  } catch {
    return null;
  }
}

async function getTauriEventListen(): Promise<TauriEventModule['listen'] | null> {
  const globalListen = getTauriBridge()?.event?.listen;
  if (globalListen) {
    return globalListen as TauriEventModule['listen'];
  }

  try {
    tauriEventModule ??= import('@tauri-apps/api/event') as Promise<TauriEventModule>;
    const mod = await tauriEventModule;
    return mod.listen;
  } catch {
    return null;
  }
}

async function getTauriDialogOpen(): Promise<TauriDialogModule['open'] | null> {
  const globalOpen = getTauriBridge()?.dialog?.open;
  if (globalOpen) {
    return globalOpen as TauriDialogModule['open'];
  }

  try {
    tauriDialogModule ??= import('@tauri-apps/plugin-dialog') as Promise<TauriDialogModule>;
    const mod = await tauriDialogModule;
    return mod.open;
  } catch {
    return null;
  }
}

function ensureReceiver() {
  if (receiverInitialized) {
    return;
  }

  receiverInitialized = true;
  const receiver = getExternalBridge().receiveMessage;
  if (!receiver) {
    if (detectRuntime() === 'webview2') {
      bridgeLog('webview2 runtime without receiveMessage handler');
    }
    return;
  }

  bridgeLog('webview2 receiver attached');

  receiver((raw: string) => {
    try {
      const msg = JSON.parse(raw) as HostEventMessage | HostResponseMessage;

      if (msg.type === 'event') {
        emitLocalEvent(msg.event, msg.payload);
        return;
      }

      if (msg.type === 'response') {
        const req = pending.get(msg.id);
        if (!req) {
          return;
        }
        pending.delete(msg.id);
        if (msg.error) {
          req.reject(new Error(msg.error));
        } else {
          req.resolve(msg.result);
        }
      }
    } catch {
      // ignore malformed bridge messages
    }
  });
}

function emitLocalEvent(event: string, payload: unknown) {
  const set = listeners.get(event);
  if (!set) {
    return;
  }

  set.forEach((cb) => cb({ payload }));
}

async function registerTauriListenerIfNeeded(event: string) {
  if (tauriUnlisten.has(event)) {
    return;
  }

  const listenFn = await getTauriEventListen();
  if (!listenFn) {
    bridgeLog(`tauri listen unavailable for event '${event}'`);
    return;
  }

  const unlisten = await listenFn(event, (message) => {
    emitLocalEvent(event, message.payload);
  });

  tauriUnlisten.set(event, unlisten);
  bridgeLog(`tauri listener registered for '${event}'`);
}

function unregisterTauriListenerIfUnused(event: string) {
  const set = listeners.get(event);
  if (set && set.size > 0) {
    return;
  }

  const unlisten = tauriUnlisten.get(event);
  if (!unlisten) {
    return;
  }

  tauriUnlisten.delete(event);
  unlisten();
  bridgeLog(`tauri listener removed for '${event}'`);
}

export async function invoke<T = unknown>(command: string, args: Record<string, unknown> = {}): Promise<T> {
  ensureReceiver();

  const runtime = detectRuntime();
  if (runtime === 'tauri') {
    const invokeFn = await getTauriCoreInvoke();
    if (!invokeFn) {
      throw new Error('Tauri invoke API not available');
    }
    const result = await invokeFn(command, args);
    return result as T;
  }

  const bridge = getExternalBridge();
  if (runtime !== 'webview2' || !bridge.sendMessage) {
    throw new Error('Desktop bridge not available');
  }

  const id = ++requestId;
  const payload = JSON.stringify({ type: 'invoke', id, command, args });

  const result = await new Promise<unknown>((resolve, reject) => {
    pending.set(id, { resolve, reject });
    bridge.sendMessage!(payload);
  });

  return result as T;
}

export async function listen<T = unknown>(event: string, callback: Listener<T>): Promise<() => void> {
  ensureReceiver();

  if (!listeners.has(event)) {
    listeners.set(event, new Set());
  }
  listeners.get(event)!.add(callback as Listener);

  if (detectRuntime() === 'tauri') {
    await registerTauriListenerIfNeeded(event);
  }

  return () => {
    const set = listeners.get(event);
    if (!set) {
      return;
    }
    set.delete(callback as Listener);
    if (set.size === 0) {
      listeners.delete(event);
    }
    unregisterTauriListenerIfUnused(event);
  };
}

export async function open(options: {
  multiple?: boolean;
  filters?: Array<{ name: string; extensions: string[] }>;
}): Promise<string | string[] | null> {
  if (detectRuntime() === 'tauri') {
    const tauriOpen = await getTauriDialogOpen();
    if (tauriOpen) {
      return tauriOpen(options ?? {});
    }
  }

  return invoke<string | string[] | null>('open_file_dialog', options ?? {});
}
