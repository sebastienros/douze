// Douze live reload, injected by `douze serve` in every HTML page: the server sends the URLs of the files each build
// changed; the page reloads when it is one of them or when a script changed, and swaps its stylesheets in place when
// only stylesheets changed. A WebSocket, not an event stream: it does not hold one of the browser's 6 connections per
// host, so open pages never stall the requests of others.
(() => {
  let session;
  let socket;
  let hidden = false;
  const page = path => path.replace(/\/index\.html$/, '/');

  function connect() {
    socket = new WebSocket(new URL('/__douze/socket', location.href.replace(/^http/, 'ws')));
    socket.addEventListener('message', e => receive(JSON.parse(e.data)));
    // The server stopped or restarted: try again every second, the hello of a new session reloads the page.
    socket.addEventListener('close', () => {
      if (!hidden) {
        setTimeout(connect, 1000);
      }
    });
  }

  function receive(message) {
    // A new session means the server restarted: the page may have changed while it was down.
    if (message.type === 'hello') {
      if (session !== undefined && session !== message.session) {
        location.reload();
      }
      session = message.session;
      return;
    }

    if (message.type !== 'changed') {
      return;
    }

    const here = page(decodeURI(location.pathname));

    if (message.changed.some(url => url === here || url.endsWith('.js') || url.endsWith('.mjs'))) {
      location.reload();
      return;
    }

    const styles = message.changed.filter(url => url.endsWith('.css'));

    for (const link of document.querySelectorAll('link[rel~="stylesheet"]')) {
      const url = new URL(link.href, location.href);
      if (url.origin === location.origin && styles.includes(decodeURI(url.pathname))) {
        url.searchParams.set('douze-reload', Date.now().toString());
        link.href = url.href;
      }
    }
  }

  // Close the socket when the page is left, so that the server forgets it at once; reconnect when the page comes
  // back from the browser's back/forward cache.
  addEventListener('pagehide', () => {
    hidden = true;
    socket.close();
  });
  addEventListener('pageshow', e => {
    if (e.persisted) {
      hidden = false;
      connect();
    }
  });

  connect();
})();
