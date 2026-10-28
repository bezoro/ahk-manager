import { randomUUID } from 'node:crypto';
import type { UpdateRelease, UpdateState } from '../shared/updates.js';
import { compareVersions, validUpdateVersion, type RemoteUpdateRelease } from './github-updates.js';
import type { CachedInstaller } from './update-store.js';

export interface UpdateStorePort {
  readToken(): Promise<string | null>; saveToken(token: string | null): Promise<void>;
  cached(): Promise<CachedInstaller[]>; verify(installer: CachedInstaller): Promise<void>;
  cache(version: string, source: string, sha256: string, bytes: number): Promise<CachedInstaller>;
}
export interface UpdateBoundary {
  version: string; store: UpdateStorePort;
  releases(token: string | null): Promise<RemoteUpdateRelease[]>;
  download(release: RemoteUpdateRelease, token: string | null, progress: (percent: number) => void): Promise<string>;
  preflight(operationId: string, phase: 'begin' | 'commit'): Promise<boolean>;
  confirm(release: UpdateRelease): Promise<boolean>;
  apply(operationId: string, target: CachedInstaller): Promise<void>;
  changed(state: UpdateState): void;
  recovery?(): Promise<string | null>;
}
const active = (state: UpdateState): boolean => ['checking', 'downloading', 'installing'].includes(state.phase);
const safeError = (error: unknown, fallback: string): string => error instanceof Error && !/https?:|token\s+[a-z0-9_]{20}|gh[pousr]_|github_pat_/i.test(error.message) ? error.message.slice(0, 2000) : fallback;

/** Background checks never download or install. Installation is an explicit serialized action. */
export class UpdateService {
  private state: UpdateState;
  private remote: RemoteUpdateRelease[] = [];
  private local: CachedInstaller[] = [];
  private token: string | null = null;
  private installing = false;
  private configuringCredential = false;
  constructor(private readonly boundary: UpdateBoundary) {
    this.state = { currentVersion: boundary.version, phase: 'idle', availableVersion: null, lastCheckedAt: null, error: null, accessConfigured: false, progress: null, releases: [] };
  }
  get snapshot(): UpdateState { return structuredClone(this.state); }
  get busy(): boolean { return this.installing || this.configuringCredential || active(this.state); }
  private publish(patch: Partial<UpdateState> = {}): UpdateState {
    this.state = { ...this.state, ...patch };
    const versions = new Map<string, UpdateRelease>();
    for (const cached of this.local.filter(item => validUpdateVersion(item.version))) versions.set(cached.version, { id: `local:${cached.version}`, version: cached.version, publishedAt: null, notes: 'Instalador preservado neste computador.', commit: null, installed: cached.version === this.boundary.version, canInstall: cached.version !== this.boundary.version, cached: true });
    for (const release of this.remote) versions.set(release.version, { id: release.id, version: release.version, publishedAt: release.publishedAt, notes: release.notes, commit: release.commit, installed: release.version === this.boundary.version, canInstall: release.version !== this.boundary.version, cached: this.local.some(item => item.version === release.version && item.sha256 === release.sha256 && item.bytes === release.bytes) });
    if (!versions.has(this.boundary.version)) versions.set(this.boundary.version, { id: 'installed', version: this.boundary.version, publishedAt: null, notes: 'Versão em execução.', commit: null, installed: true, canInstall: false, cached: false });
    this.state.releases = [...versions.values()].sort((a, b) => compareVersions(b.version, a.version)).slice(0, 1000);
    this.state.availableVersion = this.state.releases.find(item => compareVersions(item.version, this.boundary.version) > 0 && item.canInstall)?.version ?? null;
    this.boundary.changed(this.snapshot);
    return this.snapshot;
  }
  async initialize(): Promise<UpdateState> {
    try { this.token = await this.boundary.store.readToken(); this.local = await this.boundary.store.cached(); return this.publish({ accessConfigured: this.token !== null, error: await this.boundary.recovery?.() ?? null }); }
    catch (error) { return this.publish({ phase: 'error', error: safeError(error, 'Não foi possível carregar o histórico de atualizações.') }); }
  }
  async setToken(value: string | null): Promise<UpdateState> {
    if (this.busy) return this.snapshot;
    this.configuringCredential = true;
    this.publish({ phase: 'checking', error: null });
    try {
      await this.boundary.store.saveToken(value); this.token = value; this.remote = [];
      this.publish({ accessConfigured: value !== null, error: null, phase: 'idle', lastCheckedAt: null });
    } catch { return this.publish({ phase: 'error', error: 'Não foi possível salvar o acesso protegido ao GitHub. Confira o token e a proteção de credenciais do Windows.' }); }
    finally { this.configuringCredential = false; }
    return this.check();
  }
  async check(): Promise<UpdateState> {
    if (this.busy) return this.snapshot;
    this.publish({ phase: 'checking', error: null, progress: null });
    try {
      this.local = await this.boundary.store.cached();
      this.remote = await this.boundary.releases(this.token);
      const recovery = await this.boundary.recovery?.() ?? null;
      return this.publish({ phase: recovery ? 'error' : 'idle', error: recovery, lastCheckedAt: new Date().toISOString() });
    } catch (error) { return this.publish({ phase: 'error', error: safeError(error, 'Não foi possível consultar as atualizações. Tente novamente mais tarde.') }); }
  }
  async install(id: string): Promise<UpdateState> {
    if (this.busy) return this.snapshot;
    const choice = this.state.releases.find(item => item.id === id && item.canInstall);
    if (!choice) return this.publish({ phase: 'error', error: 'Essa versão não está mais disponível no histórico. Verifique as atualizações.' });
    this.installing = true;
    const operationId = randomUUID();
    try {
      if (!await this.boundary.preflight(operationId, 'begin')) return this.publish({ phase: 'error', error: 'Salve ou descarte as alterações e aguarde as operações antes de atualizar.' });
      if (!await this.boundary.confirm(choice)) return this.snapshot;
      this.publish({ phase: 'downloading', error: null, progress: 0 });
      const remote = this.remote.find(item => item.id === id);
      let target = this.local.find(item => item.version === choice.version && (!remote || item.sha256 === remote.sha256 && item.bytes === remote.bytes));
      if (target) { try { await this.boundary.store.verify(target); } catch (error) { if (!remote) throw error; target = undefined; } }
      if (!target) {
        if (!remote) throw new Error('O instalador dessa versão não está disponível neste computador.');
        const file = await this.boundary.download(remote, this.token, percent => this.publish({ progress: Math.max(0, Math.min(100, percent)) }));
        target = await this.boundary.store.cache(remote.version, file, remote.sha256, remote.bytes);
      }
      await this.boundary.store.verify(target);
      this.local = await this.boundary.store.cached();
      if (!await this.boundary.preflight(operationId, 'commit')) return this.publish({ phase: 'error', progress: null, error: 'O instalador foi preservado. Feche os diálogos, salve as alterações e tente instalar novamente.' });
      this.publish({ phase: 'installing', progress: 100 });
      await this.boundary.apply(operationId, target);
      return this.snapshot;
    } catch (error) { return this.publish({ phase: 'error', progress: null, error: safeError(error, 'A atualização não foi concluída. A instalação e os registros de recuperação foram preservados.') }); }
    finally { this.installing = false; }
  }
}
