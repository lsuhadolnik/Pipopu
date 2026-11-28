const instructions = document.getElementById('windows-download');

if (typeof instructions.showModal === 'function') {
  for (const link of document.querySelectorAll('[data-windows-download]')) {
    link.addEventListener('click', (event) => {
      if (event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
      event.preventDefault();
      instructions.showModal();
    });
  }
}
