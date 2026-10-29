import { nativeTheme, systemPreferences, type BrowserWindow } from 'electron';
import os from 'node:os';
import type { ManagerSettings, ThemeState } from '../../shared/contracts.js';

/** Owns Windows material and title-bar styling; renderer styling follows the published state. */
export class DesktopTheme {
  private material: ThemeState['material'] = 'solid';

  constructor(
    private readonly getWindow: () => BrowserWindow | null,
    private readonly getSettings: () => ManagerSettings | undefined,
    private readonly publish: (state: ThemeState) => void,
  ) {}

  get backgroundMaterial(): ThemeState['material'] {
    return this.material;
  }

  get state(): ThemeState {
    const color = systemPreferences.getAccentColor();
    return {
      dark: nativeTheme.shouldUseDarkColors,
      highContrast: nativeTheme.shouldUseHighContrastColors,
      reducedMotion: !systemPreferences.getAnimationSettings().shouldRenderRichAnimation,
      accent: `#${color.slice(0, 6)}`,
      material: this.material,
    };
  }

  private get supportsMica(): boolean {
    return (
      Number(os.release().split('.')[2] ?? 0) >= 22621 && !nativeTheme.shouldUseHighContrastColors
    );
  }

  initialize(window: BrowserWindow): void {
    if (this.supportsMica) {
      try {
        window.setBackgroundMaterial('mica');
        this.material = 'mica';
      } catch {
        this.material = 'solid';
      }
    }
    if (this.material === 'solid') {
      window.setBackgroundColor(nativeTheme.shouldUseDarkColors ? '#202020' : '#f7f7f7');
    }
  }

  update = (): void => {
    const window = this.getWindow();
    if (!window || window.isDestroyed()) return;
    const preference = this.getSettings()?.theme;
    nativeTheme.themeSource =
      preference === 'Light' ? 'light' : preference === 'Dark' ? 'dark' : 'system';
    const supportsMica = this.supportsMica;
    try {
      window.setBackgroundMaterial(supportsMica ? 'mica' : 'none');
      this.material = supportsMica ? 'mica' : 'solid';
    } catch {
      this.material = 'solid';
    }
    window.setBackgroundColor(
      this.material === 'mica'
        ? '#00000000'
        : nativeTheme.shouldUseDarkColors
          ? '#202020'
          : '#f7f7f7',
    );
    const state = this.state;
    window.setTitleBarOverlay({
      height: 44,
      color:
        this.material === 'mica' && !state.highContrast
          ? '#00000000'
          : state.dark
            ? '#202020'
            : '#f7f7f7',
      symbolColor: state.dark ? '#ffffff' : '#1b1b1b',
    });
    this.publish(state);
  };
}
