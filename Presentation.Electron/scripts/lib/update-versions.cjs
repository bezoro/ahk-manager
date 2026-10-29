function validUpdateVersion(value) {
  if (typeof value !== 'string' || value.length > 64) return false;
  const match =
    /^(0|[1-9]\d{0,9})\.(0|[1-9]\d{0,9})\.(0|[1-9]\d{0,9})(?:-test\.([1-9]\d{0,9})\.([1-9]\d{0,9})\.([1-9]\d{0,9}))?$/.exec(
      value,
    );
  return (
    !!match &&
    match[0] === value &&
    match
      .slice(1)
      .filter((part) => part !== undefined)
      .every((part) => Number(part) <= 2147483647)
  );
}

module.exports = { validUpdateVersion };
