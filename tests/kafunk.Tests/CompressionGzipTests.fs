﻿module CompressionGzipTests

open Kafunk
open NUnit.Framework
open System
open System.Text

[<Test>]
[<Category("Compression")>]
let ``Compression.GZip should work`` () =

    let messageBytes = [| 1uy; 2uy; 3uy; 4uy; 2uy; 6uy; 8uy |]
    let message2Bytes = [| 1uy; 2uy; 3uy; 2uy |]

    let message = Message.create (Binary.ofArray messageBytes) (Binary.empty) None
    let message2 = Message.create (Binary.ofArray message2Bytes) (Binary.empty) None
    
    let inputMessage =
        Compression.GZip.compress 0s (MessageSet.ofMessages [message; message2])

    let outputMessageSet =
        Compression.GZip.decompress 0s inputMessage

    let messages = outputMessageSet.messages
    Assert.IsTrue (messages.Length = 2)
    let (offset, size, msg) = messages.[0]
    let (offset2, size2, msg2) = messages.[1]
    Assert.IsTrue (msg.value |> Binary.toArray = messageBytes)
    Assert.IsTrue (msg2.value |> Binary.toArray = message2Bytes)

