import type { TgConfig } from './config';

export interface TgState {
  cfg: TgConfig;
  sid: string;
  nonce: string; // '' until /i/init resolves; '' forever if it fails
  storageTs: number | null; // consumed by SDK-04
  storageSig: string | null; // consumed by SDK-04
  initSettled: boolean;
  seq: number; // next envelope sequence number, starts at 0
}

export let state: TgState; // assigned once in bootstrap

export function initState(cfg: TgConfig, sid: string): void {
  state = {
    cfg,
    sid,
    nonce: '',
    storageTs: null,
    storageSig: null,
    initSettled: false,
    seq: 0,
  };
}
