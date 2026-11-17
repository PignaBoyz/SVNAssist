import asyncio, sys, time

DELAY = float(sys.argv[1]) / 1000.0  # one-way delay in seconds
LISTEN = int(sys.argv[2])
TARGET = int(sys.argv[3])


async def pump(reader, writer):
    q = asyncio.Queue()

    async def producer():
        try:
            while True:
                data = await reader.read(65536)
                if not data:
                    break
                await q.put((time.monotonic() + DELAY, data))
        finally:
            await q.put(None)

    async def consumer():
        while True:
            item = await q.get()
            if item is None:
                break
            due, data = item
            wait = due - time.monotonic()
            if wait > 0:
                await asyncio.sleep(wait)
            writer.write(data)
            await writer.drain()
        try:
            writer.close()
        except Exception:
            pass

    await asyncio.gather(producer(), consumer())


async def handle(creader, cwriter):
    sreader, swriter = await asyncio.open_connection('127.0.0.1', TARGET)
    await asyncio.gather(pump(creader, swriter), pump(sreader, cwriter))


async def main():
    server = await asyncio.start_server(handle, '127.0.0.1', LISTEN)
    async with server:
        await server.serve_forever()


asyncio.run(main())
