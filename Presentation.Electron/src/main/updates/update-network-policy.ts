import type { Session } from 'electron';

/** Restrict the update session without granting renderer network access. */
export function configureUpdateNetwork(network: Session): void {
  network.setPermissionRequestHandler((_contents, _permission, callback) => callback(false));
  network.setPermissionCheckHandler(() => false);
  network.webRequest.onBeforeRequest((details, callback) => {
    let allowed = false;
    try {
      const url = new URL(details.url);
      allowed =
        url.protocol === 'https:' &&
        !url.username &&
        !url.password &&
        !url.port &&
        ((url.hostname === 'api.github.com' &&
          (url.pathname === '/repos/bezoro/ahk-manager/releases' ||
            url.pathname.startsWith('/repos/bezoro/ahk-manager/releases/'))) ||
          ['release-assets.githubusercontent.com', 'objects.githubusercontent.com'].includes(
            url.hostname,
          ));
    } catch {
      /* Only the fixed update origin is permitted. */
    }
    callback({ cancel: !allowed });
  });
  network.webRequest.onBeforeSendHeaders((details, callback) => {
    const headers = { ...details.requestHeaders };
    const assetRequest = new URL(details.url).hostname !== 'api.github.com';
    for (const name of Object.keys(headers)) {
      const normalized = name.toLowerCase();
      if (
        ['authorization', 'cookie'].includes(normalized) ||
        (assetRequest && normalized === 'proxy-authorization')
      )
        delete headers[name];
    }
    callback({ requestHeaders: headers });
  });
}
