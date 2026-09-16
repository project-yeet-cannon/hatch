// A copy button on every code block, and nothing else. Progressive: the page
// reads the same without it.
(function () {
  if (!navigator.clipboard) return;
  document.querySelectorAll('div.highlighter-rouge, pre.highlight').forEach(function (block) {
    var pre = block.querySelector('pre') || block;
    if (!pre || block.querySelector('.copy')) return;
    var button = document.createElement('button');
    button.type = 'button';
    button.className = 'copy';
    button.textContent = 'Copy';
    button.addEventListener('click', function () {
      navigator.clipboard.writeText(pre.innerText.replace(/\n$/, '')).then(function () {
        button.textContent = 'Copied';
        setTimeout(function () { button.textContent = 'Copy'; }, 1500);
      });
    });
    block.classList.add('has-copy');
    block.appendChild(button);
  });
})();
