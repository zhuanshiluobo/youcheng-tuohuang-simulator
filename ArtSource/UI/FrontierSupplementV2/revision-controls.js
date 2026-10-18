document.querySelectorAll('.check').forEach(input => {
  input.checked = input.defaultChecked;
  const shell = document.createElement('span');
  shell.className = 'check-shell';
  input.replaceWith(shell);
  shell.append(input);
  const visual = document.createElement('span');
  visual.className = 'check-visual';
  visual.setAttribute('aria-hidden', 'true');
  visual.innerHTML = '<span class="check-face"></span><span class="check-mark"></span>';
  shell.append(visual);
  const stateLabel = shell.closest('label')?.querySelector('.check-state-label');
  const updateLabel = () => {
    if (stateLabel && !input.disabled) stateLabel.textContent = input.checked ? '选中' : '未选中';
  };
  input.addEventListener('change', updateLabel);
  updateLabel();
});
