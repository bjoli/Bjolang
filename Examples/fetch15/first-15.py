#!/usr/bin/env python3
"""Fetch the given urls, at most 8 at a time, and stop once 15 have landed.

    python3 Examples/fetch15/first-15.py https://example.com https://...

The Bjolang, Clojure and F# versions beside this one all build a feeder, a crew
of workers and a channel between them, because that is what their concurrency
primitives are. Python's are a semaphore and a completion iterator, so the crew
here is a number and the channel is a for loop.
"""

import asyncio
import sys

import aiohttp

WANTED = 15
AT_ONCE = 8
TIMEOUT = aiohttp.ClientTimeout(total=15)


async def fetch(
    session: aiohttp.ClientSession, url: str, gate: asyncio.Semaphore
) -> str:
    """Read the whole body, holding one of the gate's slots while it runs."""
    async with gate, session.get(url) as response:
        await response.text()
    return url


async def first_to_land(urls: list[str], wanted: int, at_once: int) -> list[str]:
    """The first `wanted` urls to answer, in the order they answered.

    Fewer if too many fail: the loop ends when the requests do.
    """
    gate = asyncio.Semaphore(at_once)

    async with aiohttp.ClientSession(timeout=TIMEOUT) as session:
        pending = [asyncio.create_task(fetch(session, url, gate)) for url in urls]
        landed = []
        try:
            async for task in asyncio.as_completed(pending):
                try:
                    landed.append(await task)
                except (aiohttp.ClientError, TimeoutError, UnicodeDecodeError):
                    continue
                if len(landed) == wanted:
                    break
        finally:
            # Leaving the loop early leaves requests in flight, and the session
            # closes on the way out of the `async with`. Cancel them first, then
            # wait: without the second half those sockets tear down under it.
            for task in pending:
                task.cancel()
            await asyncio.gather(*pending, return_exceptions=True)
        return landed


async def main(urls: list[str]) -> None:
    for url in await first_to_land(urls, WANTED, AT_ONCE):
        print(url)


if __name__ == "__main__":
    asyncio.run(main(sys.argv[1:]))
