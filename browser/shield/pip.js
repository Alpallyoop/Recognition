// Picture-in-picture. Function body: wrapped as (async function(g){ ... })(window) and run with a user gesture from the browser.
// Returns 'opened', 'closed', 'novideo' or 'error:<name>'. Only videos in the top page are found (a video inside another site's frame is not).
var doc = g.document;
try {
  if (!doc.pictureInPictureEnabled) return 'error:unsupported';
  var cur = doc.pictureInPictureElement;
  if (cur) { await doc.exitPictureInPicture(); return 'closed'; }
  var vs = Array.prototype.slice.call(doc.querySelectorAll('video')).filter(function (v) { return v.readyState > 0 && !v.ended; });
  if (!vs.length) return 'novideo';
  function area(v) { var r = v.getBoundingClientRect(); return (r.width || 0) * (r.height || 0); }
  vs.sort(function (a, b) { var pa = a.paused ? 0 : 1, pb = b.paused ? 0 : 1; return pb - pa || area(b) - area(a); });
  var v = vs[0];
  try { v.disablePictureInPicture = false; v.removeAttribute('disablepictureinpicture'); } catch (e) {}
  await v.requestPictureInPicture();
  return 'opened';
} catch (e) { return 'error:' + ((e && e.name) || 'unknown'); }
