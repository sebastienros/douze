// Douze live reload, injected by `douze serve` in every HTML page: the server sends the URLs of the files each build
// changed; the page reloads when it is one of them or when a script changed, and swaps its stylesheets in place when
// only stylesheets changed.
(() => {
  let session;
  const page = path => path.replace(/\/index\.html$/, '/');
  const events = new EventSource('/__douze/events');

  // A new session means the server restarted: the page may have changed while it was down.
  events.addEventListener('hello', e => {
    const id = JSON.parse(e.data).session;
    if (session !== undefined && session !== id) {
      location.reload();
    }
    session = id;
  });

  events.addEventListener('message', e => {
    const changed = JSON.parse(e.data).changed;
    const here = page(decodeURI(location.pathname));

    if (changed.some(url => url === here || url.endsWith('.js') || url.endsWith('.mjs'))) {
      location.reload();
      return;
    }

    const styles = changed.filter(url => url.endsWith('.css'));

    for (const link of document.querySelectorAll('link[rel~="stylesheet"]')) {
      const url = new URL(link.href, location.href);
      if (url.origin === location.origin && styles.includes(decodeURI(url.pathname))) {
        url.searchParams.set('douze-reload', Date.now().toString());
        link.href = url.href;
      }
    }
  });
})();
