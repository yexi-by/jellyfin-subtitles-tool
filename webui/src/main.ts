import css from './style.css?inline';
import { emptyMessage, readEvents, visibleCandidates, type Candidate } from './logic';

interface ApiClient { getUrl(path: string): string; accessToken(): string; getCurrentUserId(): string }
declare global { interface Window { ApiClient?: ApiClient; __subtitlesToolLoaded?: boolean } }
interface MediaInfo { fileName: string; mediaSourceId: string; hasRecord: boolean; subtitles: string[] }
class RequestError extends Error { constructor(message: string, readonly status: number) { super(message); } }

function node<K extends keyof HTMLElementTagNameMap>(tag: K, className = '', text = ''): HTMLElementTagNameMap[K] {
  const result = document.createElement(tag); result.className = className; result.textContent = text; return result;
}
function button(text: string, action: () => void, className = ''): HTMLButtonElement {
  const result = node('button', className, text); result.type = 'button'; result.addEventListener('click', action); return result;
}
function itemId(): string | null {
  const hash = location.hash;
  if (!/\/details(?:\?|$)|\/item(?:\?|$)/.test(hash)) return null;
  return new URLSearchParams(hash.slice(hash.indexOf('?') + 1)).get('id');
}
function page(): HTMLElement | null { return [...document.querySelectorAll<HTMLElement>('.itemDetailPage')].find(element => !element.classList.contains('hide') && element.getClientRects().length > 0) ?? null; }
function sourceId(): string | undefined { return page()?.querySelector<HTMLSelectElement>('.selectSource')?.value || undefined; }
async function request(path: string, signal: AbortSignal, body?: unknown): Promise<Response> {
  const client = window.ApiClient;
  if (!client) throw new Error('Jellyfin 尚未完成加载，请稍后重试。');
  const response = await fetch(client.getUrl(path), { method: body === undefined ? 'GET' : 'POST', signal, headers: { Authorization: `MediaBrowser Token="${client.accessToken()}"`, ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) }, body: body === undefined ? undefined : JSON.stringify(body) });
  if (!response.ok) {
    const data = await response.json().catch(() => null) as { message?: string } | null;
    throw new RequestError(data?.message ?? (response.status === 403 ? '当前账号没有字幕管理权限。' : `请求失败（${response.status}），请重试。`), response.status);
  }
  return response;
}

class Panel {
  private readonly host = node('div');
  private readonly shadow = this.host.attachShadow({ mode: 'open' });
  private readonly dialog = node('dialog');
  private readonly title = node('h1', '', '正在读取当前视频…');
  private readonly existing = node('p', 'muted existing');
  private readonly statusText = node('p', 'status-text', '正在搜索字幕…');
  private readonly status = node('div', 'status');
  private readonly progress = node('progress');
  private readonly list = node('div', 'content');
  private readonly confirm = node('div', 'confirm');
  private readonly abort = new AbortController();
  private searchAbort?: AbortController;
  private candidates: Candidate[] = [];
  private chineseOnly = true;
  private completed = false;
  private downloading = false;
  private readonly chineseButton = button('中文 / 双语', () => this.filter(true), 'filter');
  private readonly allButton = button('全部', () => this.filter(false), 'filter');
  private readonly retry = button('重新搜索', () => void this.search(), 'retry');
  private readonly focusBefore = document.activeElement instanceof HTMLElement ? document.activeElement : null;
  private readonly root: string;
  private readonly selectedSource = sourceId();

  constructor(id: string, private readonly onClose: () => void) {
    this.root = `SubtitlesTool/Items/${id}`;
    const style = node('style'); style.textContent = css; this.shadow.append(style, this.dialog);
    this.dialog.setAttribute('aria-labelledby', 'st-title'); this.title.id = 'st-title';
    this.dialog.addEventListener('cancel', event => { event.preventDefault(); this.close(true); });
    const layout = node('div', 'panel');
    const header = node('header'); const heading = node('div'); heading.append(node('p', 'eyebrow', '外挂字幕'), this.title, this.existing);
    const close = button('×', () => this.close(true), 'close'); close.setAttribute('aria-label', '关闭字幕面板'); header.append(heading, close);
    const toolbar = node('div', 'toolbar'); toolbar.append(this.chineseButton, this.allButton, this.retry);
    this.status.setAttribute('role', 'status'); this.status.setAttribute('aria-live', 'polite');
    this.progress.max = 100; this.progress.hidden = true; this.progress.setAttribute('aria-label', '准备字幕信息的进度'); this.status.append(this.statusText, this.progress);
    this.confirm.hidden = true; this.confirm.setAttribute('role', 'alertdialog'); this.confirm.setAttribute('aria-label', '替换已有字幕');
    layout.append(header, toolbar, this.status, this.list, this.confirm); this.dialog.append(layout);
    document.body.append(this.host); this.dialog.showModal();
    history.pushState({ ...history.state, subtitlesTool: true }, '', location.href);
    this.filter(true); void this.initialize();
  }
  private async initialize(): Promise<void> {
    try { await this.loadInfo(); await this.search(); }
    catch (error) { this.showError(error); }
  }
  private async loadInfo(): Promise<void> {
    const suffix = this.selectedSource ? '?mediaSourceId=' + encodeURIComponent(this.selectedSource) : '';
    const info = await (await request(this.root + suffix, this.abort.signal)).json() as MediaInfo;
    if (this.abort.signal.aborted) return;
    this.title.textContent = info.fileName;
    this.existing.textContent = info.subtitles.length ? '已有外挂字幕：' + info.subtitles.join('、') : '选择一条字幕，保存到视频所在目录。';
  }
  private setStatus(message: string, error = false): void { this.statusText.textContent = message; this.status.classList.toggle('error', error); }
  private showError(error: unknown): void {
    if (this.abort.signal.aborted || (error instanceof DOMException && error.name === 'AbortError')) return;
    this.progress.hidden = true; this.setStatus(error instanceof Error ? error.message : '操作失败，请重试。', true);
  }
  private filter(chinese: boolean): void {
    this.chineseOnly = chinese; this.chineseButton.setAttribute('aria-pressed', String(chinese)); this.allButton.setAttribute('aria-pressed', String(!chinese)); this.render();
  }
  private render(): void {
    this.list.replaceChildren(); this.allButton.textContent = this.completed ? `全部（${this.candidates.length}）` : '全部';
    const visible = visibleCandidates(this.candidates, this.chineseOnly);
    if (!visible.length && this.completed) {
      const empty = node('div', 'empty'); empty.append(node('p', '', emptyMessage(this.candidates, this.chineseOnly)));
      if (this.candidates.length) empty.append(button('查看全部字幕', () => this.filter(false), 'primary'));
      this.list.append(empty);
    }
    for (const candidate of visible) {
      const card = node('article', 'card'); const details = node('div', 'details'); details.append(node('h2', 'name', candidate.name || '未命名字幕'));
      const badges = node('div', 'badges'); badges.append(node('span', 'badge', candidate.format.toUpperCase()), node('span', 'badge', candidate.languages.join(' / ') || '未标注语言'), node('span', '', `源评分 ${candidate.score}`)); details.append(badges);
      const save = button('下载', () => void this.download(candidate, false), 'primary'); save.disabled = this.downloading; save.setAttribute('aria-label', `下载 ${candidate.name}`); card.append(details, save); this.list.append(card);
    }
  }
  private async search(): Promise<void> {
    if (this.downloading) return;
    this.searchAbort?.abort(); const current = new AbortController(); this.searchAbort = current;
    const cancel = () => current.abort(); this.abort.signal.addEventListener('abort', cancel, { once: true });
    this.candidates = []; this.completed = false; this.confirm.hidden = true; this.list.inert = false; this.retry.disabled = true; this.progress.hidden = true;
    this.render(); this.setStatus('正在搜索字幕…');
    let terminal = false;
    try {
      const response = await request(this.root + '/search', current.signal, { mediaSourceId: this.selectedSource });
      if (!response.body) throw new Error('无法接收搜索结果，请重试。');
      await readEvents(response.body, event => {
        if (current.signal.aborted) return;
        if (event.type === 'progress') {
          const phase = event.phase;
          if (phase === 'hashing') {
            const total = Number(event.total); const percent = total > 0 ? Math.floor(Number(event.read) * 100 / total) : 100;
            this.progress.hidden = false; this.progress.value = percent; this.setStatus(`正在准备字幕信息 · ${percent}%　首次搜索需要读取视频，之后可直接复用。`);
          } else { this.progress.hidden = true; this.setStatus(phase === 'waiting' ? '正在等待准备字幕信息…' : '正在查询字幕源…'); }
        } else if (event.type === 'results') {
          terminal = true; this.candidates = event.candidates as Candidate[]; this.completed = true; this.progress.hidden = true; this.setStatus(`搜索完成 · ${this.candidates.length} 条结果，${this.candidates.filter(candidate => candidate.chinese).length} 条中文或双语。`); this.render();
        } else if (event.type === 'error') { terminal = true; this.showError(new Error(String(event.message))); }
      });
      if (!terminal && !current.signal.aborted) throw new Error('搜索连接提前结束，请重试。');
    } catch (error) { if (!current.signal.aborted) this.showError(error); }
    finally { this.abort.signal.removeEventListener('abort', cancel); if (this.searchAbort === current) this.retry.disabled = false; }
  }
  private askReplace(candidate: Candidate, message: string): void {
    this.setStatus('请选择是否替换已有字幕。');
    this.confirm.replaceChildren(node('p', '', message));
    const actions = node('div', 'confirm-actions');
    const cancel = button('保留原字幕', () => { this.confirm.hidden = true; this.list.inert = false; this.setStatus('已保留原字幕。'); this.list.querySelector<HTMLButtonElement>('button')?.focus(); });
    const replace = button('替换字幕', () => { this.confirm.hidden = true; this.list.inert = false; void this.download(candidate, true); }, 'primary');
    actions.append(cancel, replace); this.confirm.append(actions); this.confirm.hidden = false; this.list.inert = true; cancel.focus();
  }
  private async download(candidate: Candidate, overwrite: boolean): Promise<void> {
    this.downloading = true; this.retry.disabled = true; this.render(); this.setStatus('正在下载并保存字幕…');
    try {
      const result = await (await request(this.root + '/download', this.abort.signal, { mediaSourceId: this.selectedSource, candidateId: candidate.id, overwrite })).json() as { message: string; refreshed: boolean };
      await this.loadInfo(); this.setStatus(result.message, !result.refreshed);
    } catch (error) {
      if (error instanceof RequestError && error.status === 409) this.askReplace(candidate, error.message);
      else this.showError(error);
    } finally { this.downloading = false; this.retry.disabled = false; this.render(); }
  }
  close(back: boolean): void {
    this.abort.abort(); this.searchAbort?.abort(); this.dialog.close(); this.host.remove(); this.onClose();
    if (back && history.state?.subtitlesTool) history.back();
    if (this.focusBefore?.isConnected) this.focusBefore.focus();
  }
}

if (!window.__subtitlesToolLoaded) {
  window.__subtitlesToolLoaded = true;
  let panel: Panel | undefined;
  let checked = ''; let allowed = false; let timer: ReturnType<typeof setTimeout>;
  async function sync(): Promise<void> {
    const id = itemId(); const currentPage = page();
    if (!id || !currentPage) { checked = ''; if (panel) panel.close(false); return; }
    const client = window.ApiClient;
    if (!client?.accessToken() || !client.getCurrentUserId()) return;
    if (checked !== id) {
      checked = id; allowed = false;
      const oldButton = currentPage.querySelector<HTMLElement>('[data-subtitles-tool]'); if (oldButton) oldButton.hidden = true;
      let permission = false;
      try { await request(`SubtitlesTool/Items/${id}`, new AbortController().signal); permission = true; }
      catch { permission = false; }
      if (itemId() !== id || checked !== id) return;
      allowed = permission;
    }
    if (!allowed) return;
    const existingButton = currentPage.querySelector<HTMLElement>('[data-subtitles-tool]');
    if (existingButton) { existingButton.hidden = false; return; }
    const actions = currentPage.querySelector('.mainDetailButtons'); if (!actions) return;
    const launch = button('字幕', () => { const current = itemId(); if (!panel && current) panel = new Panel(current, () => { panel = undefined; }); }, 'button-flat button-flat-mini detailButton emby-button');
    launch.dataset.subtitlesTool = 'true'; launch.style.minWidth = '64px'; launch.style.minHeight = '44px'; actions.append(launch);
  }
  function schedule(): void { clearTimeout(timer); timer = setTimeout(() => void sync(), 100); }
  document.addEventListener('viewshow', schedule, true);
  window.addEventListener('hashchange', () => { if (panel) panel.close(false); schedule(); });
  window.addEventListener('popstate', () => { if (panel) panel.close(false); schedule(); });
  new MutationObserver(schedule).observe(document.body, { childList: true, subtree: true }); schedule();
}
