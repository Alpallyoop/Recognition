#!/usr/bin/env python3
# Regenerates the cookie-import test fixtures (chromium_cookies.sqlite, firefox_cookies.sqlite).
#
# EVERYTHING HERE IS SYNTHETIC TEST DATA, NOT A SECRET. Every host is an example./.test name, and every value is a made-up,
# self-labelling placeholder (e.g. "example-session-value"). These files exist only so the pure cookie-reading code in
# browser/SqliteReader.cs and browser/CookieImport.cs can be exercised against real SQLite files (overflow pages, a v10
# encrypted-value blob, a session cookie, an invalid host). No value decrypts to anything and none reaches any real service.
#
# Run from anywhere:  python3 make_cookie_fixtures.py   (writes next to this script)
# The browser.tests assertions in browser.tests/Program.cs depend on the exact contents below; change both together.

import sqlite3, os, datetime

HERE = os.path.dirname(os.path.abspath(__file__))

def us1601(dt):  # microseconds since 1601-01-01, as Chromium stores expires_utc
    return int((dt - datetime.datetime(1601, 1, 1)).total_seconds() * 1_000_000)

def write(path, create_sql, rows):
    if os.path.exists(path):
        os.remove(path)
    con = sqlite3.connect(path)
    con.execute(create_sql)
    ncols = len(rows[0])
    con.executemany("INSERT INTO " + create_sql.split("(")[0].split()[-1] + " VALUES(" + ",".join("?" * ncols) + ")", rows)
    con.commit()
    con.close()
    print(path, os.path.getsize(path), "bytes")

future = us1601(datetime.datetime(2030, 1, 1))
write(os.path.join(HERE, "chromium_cookies.sqlite"),
    """CREATE TABLE cookies(creation_utc INTEGER NOT NULL,host_key TEXT NOT NULL,name TEXT NOT NULL,value TEXT NOT NULL,
       path TEXT NOT NULL,expires_utc INTEGER NOT NULL,is_secure INTEGER NOT NULL,is_httponly INTEGER NOT NULL,
       samesite INTEGER NOT NULL,encrypted_value BLOB DEFAULT '',last_access_utc INTEGER NOT NULL)""",
    [
        (1, ".example.com", "sid", "example-session-value", "/", future, 1, 1, 2, b"", 1),
        (2, "example.com", "theme", "dark", "/", future, 0, 0, 1, b"", 1),
        (3, "news.example", "token", "x" * 5000, "/app", future, 1, 0, 0, b"", 1),   # large value -> overflow pages
        (4, ".site.test", "sess", "tmp", "/", 0, 0, 0, 1, b"", 1),                    # session cookie (expires_utc 0)
        (5, "enc.example", "locked", "", "/", future, 1, 1, 1, b"v10" + b"\x00" * 40, 1),  # v10 blob, undecryptable with no key
        (6, "bad host", "x", "y", "/", future, 0, 0, 1, b"", 1),                      # invalid host -> not importable
    ])

u2030 = int(datetime.datetime(2030, 1, 1, tzinfo=datetime.timezone.utc).timestamp())
write(os.path.join(HERE, "firefox_cookies.sqlite"),
    """CREATE TABLE moz_cookies(id INTEGER PRIMARY KEY,originAttributes TEXT,name TEXT,value TEXT,host TEXT,
       path TEXT,expiry INTEGER,lastAccessed INTEGER,creationTime INTEGER,isSecure INTEGER,isHttpOnly INTEGER,
       inBrowserElement INTEGER,sameSite INTEGER,rawSameSite INTEGER,schemeMap INTEGER)""",
    [
        (1, "", "ffsid", "example-firefox-value", ".moz.example", "/", u2030, 0, 0, 1, 1, 0, 2, 2, 2),
        (2, "", "pref", "en", "moz.example", "/", 0, 0, 0, 0, 0, 0, 1, 1, 2),   # session
    ])
