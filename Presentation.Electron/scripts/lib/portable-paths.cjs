function validateRelative(value) {
  const relative = value.replaceAll('\\', '/');
  if (
    !relative ||
    relative.startsWith('/') ||
    relative
      .split('/')
      .some(
        (part) =>
          !part ||
          part === '.' ||
          part === '..' ||
          !/^[A-Za-z0-9._ ()-]+$/.test(part) ||
          /[. ]$/.test(part) ||
          /^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)/i.test(part),
      )
  )
    throw new Error('Unsafe portable package path');
  return relative;
}
module.exports = { validateRelative };
