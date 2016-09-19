namespace Kafunk

open System
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading

open Kafunk
open Kafunk.Prelude
open Kafunk.Protocol

module Message =

  let create value key attrs =
    Message(0, 0y, (defaultArg attrs 0y), (defaultArg key (Binary.empty)), value)

  let ofBuffer data key =
    Message(0, 0y, 0y, (defaultArg  key (Binary.empty)), data)

  let ofBytes value key =
    let key =
      match key with
      | Some key -> Binary.ofArray key
      | None -> Binary.empty
    Message(0, 0y, 0y, key, Binary.ofArray value)

  let ofString (value:string) (key:string) =
    let value = Encoding.UTF8.GetBytes value |> Binary.ofArray
    let key = Encoding.UTF8.GetBytes key |> Binary.ofArray
    Message(0, 0y, 0y, key, value)

  let valueString (m:Message) =
    m.value |> Binary.toString

  let keyString (m:Message) =
    if isNull m.value.Array then null
    else m.value |> Binary.toString

module MessageSet =

  let ofMessage (m:Message) =
    MessageSet([| 0L, Message.size m, m |])

  let ofMessages ms =
    MessageSet(ms |> Seq.map (fun m -> 0L, Message.size m, m) |> Seq.toArray)

  /// Returns the next offset to fetch, by taking the max offset in the
  /// message set and adding one.
  let nextOffset (ms:MessageSet) =
    let maxOffset = ms.messages |> Seq.map (fun (off, _, _) -> off) |> Seq.max
    maxOffset + 1L

module ProduceRequest =

  let ofMessageSet topic partition ms requiredAcks timeout =
    ProduceRequest(
      (defaultArg requiredAcks RequiredAcks.Local),
      (defaultArg timeout 1000),
      [| topic, [| partition, MessageSet.size ms, ms |] |] )

  let ofMessageSets topic ms requiredAcks timeout =
    ProduceRequest(
      (defaultArg requiredAcks RequiredAcks.Local),
      (defaultArg timeout 1000),
      [| topic, ms |> Array.map (fun (p, ms) -> (p, MessageSet.size ms, ms)) |])

  let ofMessageSetTopics ms requiredAcks timeout =
    ProduceRequest(requiredAcks, timeout,
      ms |> Array.map (fun (t, ms) -> (t, ms |> Array.map (fun (p, ms) -> (p, MessageSet.size ms, ms)))))

module FetchRequest =

  let ofTopicPartition topic partition offset maxWaitTime minBytes maxBytesPerPartition =
    FetchRequest(-1, maxWaitTime, minBytes , [| topic, [| partition,  offset, maxBytesPerPartition |] |])

  let ofTopicPartitions topic ps maxWaitTime minBytes maxBytesPerPartition =
    FetchRequest(-1, maxWaitTime, minBytes, [| (topic, ps |> Array.map (fun (p, o) -> (p, o, maxBytesPerPartition))) |])

// Connection

/// A request/reply channel to Kafka.
// TODO: likely needs to become IDisposable, but we'll see how far we can put that off
type Chan = RequestMessage -> Async<ResponseMessage>

[<AutoOpen>]
module internal ResponseEx =

  let wrongResponse () =
    failwith (sprintf "Wrong response!")

  type RequestMessage with
    
    /// If a request does not expect a response, returns the default response.
    /// Used to generate responses for requests which don't expect a response from the server.
    static member awaitResponse (x:RequestMessage) =
      match x with
      | RequestMessage.Produce req when req.requiredAcks = RequiredAcks.None ->
        Some(ResponseMessage.ProduceResponse(new ProduceResponse([||])))
      | _ -> None

  type ResponseMessage with
    static member internal toFetch res = match res with FetchResponse x -> x | _ -> wrongResponse ()
    static member internal toProduce res = match res with ProduceResponse x -> x | _ -> wrongResponse ()
    static member internal toOffset res = match res with OffsetResponse x -> x | _ -> wrongResponse ()
    static member internal toGroupCoordinator res = match res with GroupCoordinatorResponse x -> x | _ -> wrongResponse ()
    static member internal toOffsetCommit res = match res with OffsetCommitResponse x -> x | _ -> wrongResponse ()
    static member internal toOffsetFetch res = match res with OffsetFetchResponse x -> x | _ -> wrongResponse ()
    static member internal toJoinGroup res = match res with JoinGroupResponse x -> x | _ -> wrongResponse ()
    static member internal toSyncGroup res = match res with SyncGroupResponse x -> x | _ -> wrongResponse ()
    static member internal toHeartbeat res = match res with HeartbeatResponse x -> x | _ -> wrongResponse ()
    static member internal toLeaveGroup res = match res with LeaveGroupResponse x -> x | _ -> wrongResponse ()
    static member internal toListGroups res = match res with ListGroupsResponse x -> x | _ -> wrongResponse ()
    static member internal toDescribeGroups res = match res with DescribeGroupsResponse x -> x | _ -> wrongResponse ()

    static member isError (x:ResponseMessage) =
      match x with
      | ResponseMessage.DescribeGroupsResponse r -> r.groups |> Seq.exists (fun (ec,_,_,_,_,_) -> ErrorCode.isError ec)
      | ResponseMessage.FetchResponse r -> r.topics |> Seq.exists (fun (_,xs) -> xs |> Seq.exists (fun (_,ec,_,_,_) -> ErrorCode.isError ec))
      | _ -> false
      


/// API operations on a generic request/reply channel.
module internal Api =

  let inline metadata (send:Chan) (req:Metadata.Request) =
    send (RequestMessage.Metadata req) |> Async.map (function MetadataResponse x -> x | _ -> wrongResponse ())

  let inline fetch (send:Chan) (req:FetchRequest) : Async<FetchResponse> =
    send (RequestMessage.Fetch req) |> Async.map ResponseMessage.toFetch

  let inline produce (send:Chan) (req:ProduceRequest) : Async<ProduceResponse> =
    send (RequestMessage.Produce req) |> Async.map ResponseMessage.toProduce

  let inline offset (send:Chan) (req:OffsetRequest) : Async<OffsetResponse> =
    send (RequestMessage.Offset req) |> Async.map ResponseMessage.toOffset

  let inline groupCoordinator (send:Chan) (req:GroupCoordinatorRequest) : Async<GroupCoordinatorResponse> =
    send (RequestMessage.GroupCoordinator req) |> Async.map ResponseMessage.toGroupCoordinator

  let inline offsetCommit (send:Chan) (req:OffsetCommitRequest) : Async<OffsetCommitResponse> =
    send (RequestMessage.OffsetCommit req) |> Async.map ResponseMessage.toOffsetCommit

  let inline offsetFetch (send:Chan) (req:OffsetFetchRequest) : Async<OffsetFetchResponse> =
    send (RequestMessage.OffsetFetch req) |> Async.map ResponseMessage.toOffsetFetch

  let inline joinGroup (send:Chan) (req:JoinGroup.Request) : Async<JoinGroup.Response> =
    send (RequestMessage.JoinGroup req) |> Async.map ResponseMessage.toJoinGroup

  let inline syncGroup (send:Chan) (req:SyncGroupRequest) : Async<SyncGroupResponse> =
    send (RequestMessage.SyncGroup req) |> Async.map ResponseMessage.toSyncGroup

  let inline heartbeat (send:Chan) (req:HeartbeatRequest) : Async<HeartbeatResponse> =
    send (RequestMessage.Heartbeat req) |> Async.map ResponseMessage.toHeartbeat

  let inline leaveGroup (send:Chan) (req:LeaveGroupRequest) : Async<LeaveGroupResponse> =
    send (RequestMessage.LeaveGroup req) |> Async.map ResponseMessage.toLeaveGroup

  let inline listGroups (send:Chan) (req:ListGroupsRequest) : Async<ListGroupsResponse> =
    send (RequestMessage.ListGroups req) |> Async.map ResponseMessage.toListGroups

  let inline describeGroups (send:Chan) (req:DescribeGroupsRequest) : Async<DescribeGroupsResponse> =
    send (RequestMessage.DescribeGroups req) |> Async.map ResponseMessage.toDescribeGroups

module internal Conn =

  // Let's avoid this Log vs Log.create. Just lowercase it. Shadowing the constructor is not cool.
  let private log = Log.create "Kafunk.Conn"

  let ApiVersion : ApiVersion = 0s

  /// Partitions a fetch request by topic/partition and wraps each one in a request.
  let partitionFetchReq (req:FetchRequest) =
    req.topics
    |> Seq.collect (fun (tn, ps) -> ps |> Array.map (fun (p, o, mb) -> (tn, p, o, mb)))
    |> Seq.groupBy (fun (tn, ps, _, _) ->  (tn, ps))
    |> Seq.map (fun (tp, reqs) ->
      let topics =
        reqs
        |> Seq.groupBy (fun (t, _, _, _) -> t)
        |> Seq.map (fun (t, ps) -> t, ps |> Seq.map (fun (_, p, o, mb) -> (p, o, mb)) |> Seq.toArray)
        |> Seq.toArray
      let req = new FetchRequest(req.replicaId, req.maxWaitTime, req.minBytes, topics)
      tp, RequestMessage.Fetch req)
    |> Seq.toArray

  /// Unwraps a set of responses as fetch responses and joins them into a single response.
  let concatFetchRes (rs:ResponseMessage[]) =
    rs
    |> Array.map ResponseMessage.toFetch
    |> (fun rs -> new FetchResponse(rs |> Array.collect (fun r -> r.topics)) |> ResponseMessage.FetchResponse)

  /// Partitions a produce request by topic/partition.
  let partitionProduceReq (req:ProduceRequest) =
    req.topics
    |> Seq.collect (fun (t, ps) -> ps |> Array.map (fun (p, mss, ms) -> (t, p, mss, ms)))
    |> Seq.groupBy (fun (t, p, _, _) -> (t, p))
    |> Seq.map (fun (tp, reqs) ->
      let topics =
        reqs
        |> Seq.groupBy (fun (t, _, _, _) -> t)
        |> Seq.map (fun (t, ps) -> (t, (ps |> Seq.map (fun (_, p, mss, ms) -> (p, mss, ms)) |> Seq.toArray)))
        |> Seq.toArray
      let req = new ProduceRequest(req.requiredAcks, req.timeout, topics)
      (tp, RequestMessage.Produce req))
    |> Seq.toArray

  let concatProduceResponses (rs:ResponseMessage[]) =
    rs
    |> Array.map ResponseMessage.toProduce
    |> (fun rs -> new ProduceResponse(rs |> Array.collect (fun r -> r.topics)) |> ResponseMessage.ProduceResponse)

  let concatProduceResponsesMs (rs:ProduceResponse[]) =
    new ProduceResponse(rs |> Array.collect (fun r -> r.topics)) |> ResponseMessage.ProduceResponse

  let partitionOffsetReq (req:OffsetRequest) =
    req.topics
    |> Seq.collect (fun (t, ps) -> ps |> Array.map (fun (p, tm, mo) -> (t, p, tm, mo)))
    |> Seq.groupBy (fun (t, p, _, _) -> (t, p))
    |> Seq.map (fun (tp, reqs) ->
      let topics =
        reqs
        |> Seq.groupBy (fun (t, _, _, _) -> t)
        |> Seq.map (fun (t, ps) -> (t, (ps |> Seq.map (fun (_, p, mss, ms) -> (p, mss, ms)) |> Seq.toArray)))
        |> Seq.toArray
      let req = new OffsetRequest(req.replicaId, topics)
      tp, RequestMessage.Offset req)
    |> Seq.toArray

  let concatOffsetResponses (rs:ResponseMessage[]) =
    rs
    |> Array.map ResponseMessage.toOffset
    |> (fun rs -> new OffsetResponse(rs |> Array.collect (fun r -> r.topics)) |> ResponseMessage.OffsetResponse)

  /// Performs request routing based on cluster metadata.
  /// Fetch, produce and offset requests are routed to the broker which is the leader for that topic, partition.
  /// Group related requests are routed to the respective broker.
  let route (bootstrapChan:Chan) (byTopicPartition:Map<(TopicName * Partition), Chan>) (byGroupId:Map<GroupId, Chan>) : Chan =
    // TODO: optimize single topic/partition case
    fun (req:RequestMessage) -> async {
      match req with
      | Metadata _ ->
        return! bootstrapChan req

      | Fetch req ->
        return!
          req
          |> partitionFetchReq
          |> Seq.map (fun (tp, req) ->
            match byTopicPartition |> Dict.tryGet tp with
            | Some send -> send req
            | None -> failwith "Unable to find route!")
          |> Async.Parallel
          |> Async.map concatFetchRes

      | Produce req ->
        return!
          req
          |> partitionProduceReq
          |> Seq.map (fun (tp, req) ->
            match byTopicPartition |> Dict.tryGet tp with
            | Some send -> send req
            | None -> failwith "Unable to find route!")
          |> Async.Parallel
          |> Async.map (concatProduceResponses)

      | Offset req ->
        return!
          req
          |> partitionOffsetReq
          |> Seq.map (fun (tp, req) ->
            match byTopicPartition |> Dict.tryGet tp with
            | Some send -> send req
            | None -> failwith "")
          |> Async.Parallel
          |> Async.map (concatOffsetResponses)

      | GroupCoordinator _ ->
        return! bootstrapChan req

      | OffsetCommit r ->
        match byGroupId |> Dict.tryGet r.consumerGroup with
        | Some send -> return! send req
        | None -> return failwith ""

      | OffsetFetch r ->
        match byGroupId |> Dict.tryGet r.consumerGroup with
        | Some ch -> return! ch req
        | None -> return failwith ""

      | JoinGroup r ->
        match byGroupId |> Dict.tryGet r.groupId with
        | Some send -> return! send req
        | None -> return failwith ""

      | SyncGroup r ->
        match byGroupId |> Dict.tryGet r.groupId with
        | Some send -> return! send req
        | None -> return failwith ""

      | Heartbeat r ->
        match byGroupId |> Dict.tryGet r.groupId with
        | Some send -> return! send req
        | None -> return failwith ""

      | LeaveGroup r ->
        match byGroupId |> Dict.tryGet r.groupId with
        | Some send -> return! send req
        | None -> return failwith ""

      | DescribeGroups _req ->
        // TODO
        return failwith ""

      | ListGroups _req ->
        // TODO
        return failwith "" }

  // Resource.toDVar
  // Resource.recover (access is queued)

  // operations on resource monitors.
  module Resource =


    (*
    
      - perform op
      - on failure, recover
      - recovery requests received during recovery are queued to wait of the recovery in progress, at
        which point they will receive the recovered resource. this ensures only a single resource is created.
        ...but then why not apply on creation function??

      - must change atomically with resource access

    *)

    /// Resource recovery action
    type Recovery =
      
      /// The error should be ignored.
      | Ignore

      /// The resource should be re-created.
      | Recreate

      /// The error should be escalated, notifying dependent
      /// resources.
      | Escalate     

    /// Configuration for a recoverable resource.        
    type Cfg<'r> = {
      
      /// A computation which creates a resource.
      create : Async<'r>
      
      /// A computation which handles an exception received
      /// during action upon a resource.
      /// The resource takes the returned recovery action.
      handle : ('r * exn) -> Async<Recovery>
      
      /// A heartbeat process, started each time the
      /// resource is created.
      /// If and when and heartbeat computation completes,
      /// the returned recovery action is taken.
      hearbeat : 'r -> Async<Recovery>

    }

    // When A monitors B, then A gets notified when B fails, at which point
    // A can choose to fail or continue.
    //
    // val R.monitor : R -> R -> Async<unit>
    //

    // R.stop : R -> Async<unit>
    // R.send : R -> M -> Async<unit>

    type Event =
      | Restarted
      | Escalating      
     
    /// <summary>
    /// Recoverable resource supporting the creation recoverable operations.
    /// - create - used to create the resource initially and upon recovery. Overlapped inocations
    ///   of this function are queued and given the instance being created when creation is complete.
    /// - handle - called when an exception is raised by an resource-dependent computation created
    ///   using this resrouce. If this function throws an exception, it is escalated.
    /// </summary>
    /// <notes>
    /// A resource is an entity which undergoes state changes and is used by operations.
    /// Resources can form supervision hierarchy through a message passing and reaction system.
    /// Supervision hierarchies can be used to re-cycle chains of dependent resources.
    /// </notes>
    type Resource<'r when 'r : not struct> internal (create:Async<'r>, handle:('r * exn) -> Async<Recovery>) =
      
      let Log = Log.create "Resource"
      let rsrc : 'r ref = ref Unchecked.defaultof<_>
      let mre = new ManualResetEvent(false)
      let st = ref 0 // 0 - initialized/zero | 1 - initializing
      let evt = new Event<Event>()

      member __.Events : IEvent<Event> =
        evt.Publish

      member __.Restarts : IEvent<exn> =
        failwith ""

//      member __.Monitor (r:Resource<'r>) =
//        r.Restarts.Add (fun _ ->                    
//          ())
//
//      member __.Kill () = 
//        ()
  
//      member internal __.Notify<'s> (r:Resource<'s>) =
//        evt.Publish.Add (function
//          | Restarted -> ()
//          | Escalating -> ())         

      /// Creates the resource, ensuring mutual exclusion.
      /// In this case, mutual exclusion is extended with the ability to exchange state.
      /// Protocol:
      /// - atomic { 
      ///   if ZERO then set CREATING, create and set resource, pulse waiters
      ///   else set WAITING (and wait on pulse), once wait completes, read current value
      member internal __.Create () = async {
        match Interlocked.CompareExchange (st, 1, 0) with
        | 0 ->
          Log.info "creating...."
          let! r = create
          rsrc := r
          mre.Set () |> ignore
          Interlocked.Exchange (st, 0) |> ignore
          return ()
        | _ ->        
          Log.info "waiting..."
          let! _ = Async.AwaitWaitHandle mre
          mre.Reset () |> ignore
          return () }

      /// Initiates recovery of the resource by virtue of the specified exception
      /// and executes the resulting recovery action.
      member __.Recover (ex:exn) = async {
        let r = !rsrc
        let! recovery = handle (r,ex)
        match recovery with
        | Ignore -> 
          Log.info "recovery action=ignoring..."
          return ()
        | Escalate -> 
          Log.info "recovery action=escalating..."
          //evt.Trigger (Escalating)
          raise ex
          return ()
        | Recreate ->
          Log.info "recovery action=restarting..."
          do! __.Create()
          Log.info "recovery restarted"
          return () }

      member __.Inject<'a, 'b> (op:'r -> ('a -> Async<'b>)) : 'a -> Async<'b> =
        let rec go a = async {
          let r = !rsrc
          try
            let! b = op r a
            return b
          with ex ->
            Log.info "caught exception on injected operation, calling recovery..."
            do! __.Recover ex
            Log.info "recovery complete, restarting operation..."
            return! go a }
        go

      interface IDisposable with
        member __.Dispose () = ()
    
    let recoverableRecreate (create:Async<'r>) (handleError:('r * exn) -> Async<Recovery>) = async {      
      let r = new Resource<_>(create, handleError)
      do! r.Create()
      return r }


    /// Injects a resource into a resource-dependent async function.
    /// Failures thrown by the resource-dependent computation are handled by the resource 
    /// recovery logic.
    let inject (op:'r -> ('a -> Async<'b>)) (r:Resource<'r>) : 'a -> Async<'b> =
      r.Inject op
   
      





  /// Kafka operations which can be sent to any broker in the list.
  /// This is used to restrict the operations supported by bootstrap brokers.
  module Bootstrap =
    
    let metadata (ch:Chan) (req:Metadata.Request) =
      Api.metadata ch req

    let groupCoordinator (ch:Chan) (req:GroupCoordinatorRequest) =
      Api.groupCoordinator ch req



  /// Creates a fault-tolerant channel to the specified endpoint.
  /// Recoverable failures are retried, otherwise escalated.
  let rec connect (ep:IPEndPoint, clientId:ClientId) : Async<Chan> = async {

    let receiveBufferSize = 8192

    /// Builds and connects the socket.
    let conn = async {
      // TODO: lifecycle
      let connSocket =
        new Socket(
          ep.AddressFamily,
          SocketType.Stream,
          ProtocolType.Tcp,
          NoDelay=true,
          ExclusiveAddressUse=true)
      log.info "connecting...|client_id=%s remote_endpoint=%O" clientId ep
      let! sendRcvSocket = Socket.connect connSocket ep
      log.info "connected|remote_endpoint=%O local_endpoint=%O" sendRcvSocket.RemoteEndPoint sendRcvSocket.LocalEndPoint
      return sendRcvSocket }

    let recovery (s:Socket, ex:exn) = async {
      log.info "recovering TCP connection|client_id=%s remote_endpoint=%O from error=%O" clientId ep ex
      log.trace "disposing errored connection..."
      do! Socket.disconnect s false
      s.Dispose()      
      match ex with
      | :? SocketException as _x ->
        return Resource.Recovery.Recreate
      | _ ->
        return Resource.Recovery.Escalate }

    let! sendRcvSocket = 
      Resource.recoverableRecreate 
        conn 
        recovery

    let sendErr =
      sendRcvSocket
      |> Resource.inject Socket.sendAll   

    // re-connect -> restart
    let receiveErr =
      let receive s b = 
        Socket.receive s b
        |> Async.map (fun received -> 
          if received = 0 then raise(SocketException(int SocketError.ConnectionAborted)) 
          else received)
      sendRcvSocket
      |> Resource.inject receive

    let send,receive = sendErr,receiveErr

    /// An unframed input stream.
    let inputStream =
      Socket.receiveStreamFrom receiveBufferSize receive
      |> Framing.LengthPrefix.unframe

    /// A framing sender.
    let send (data:Binary.Segment) =
      let framed = data |> Framing.LengthPrefix.frame
      send framed

    /// Encodes the request into a session layer request, keeping ApiKey as state.
    let encode (req:RequestMessage, correlationId:CorrelationId) =
      let req = Request(ApiVersion, correlationId, clientId, req)
      let sessionData = toArraySeg Request.size Request.write req
      sessionData, req.apiKey

    /// Decodes the session layer input and session state into a response.
    let decode (_, apiKey:ApiKey, buf:Binary.Segment) =
      ResponseMessage.readApiKey apiKey buf

    let session =
      Session.requestReply
        Session.corrId encode decode RequestMessage.awaitResponse inputStream send

    return session.Send }

// http://kafka.apache.org/documentation.html#connectconfigs

/// Kafka connection configuration.
type KafkaConnCfg = {
  /// The bootstrap brokers to attempt connection to.
  bootstrapServers : Uri list
  /// The client id.
  clientId : ClientId }
with

  /// Creates a Kafka configuration object given the specified list of broker hosts to bootstrap with.
  /// The first host to which a successful connection is established is used for a subsequent metadata request
  /// to build a routing table mapping topics and partitions to brokers.
  static member ofBootstrapServers (bootstrapServers:Uri list, ?clientId:ClientId) =
    { bootstrapServers = bootstrapServers
      clientId = match clientId with Some clientId -> clientId | None -> Guid.NewGuid().ToString("N") }


type KafkaRoutes () =

  // mutable routing tables

  let chanByHost : DVar<Map<Host * Port, Chan>> =
    DVar.create Map.empty

  let hostByNode : DVar<Map<NodeId, Host * Port>> =
    DVar.create Map.empty

  let nodeByTopic : DVar<Map<TopicName * Partition, NodeId>> =
    DVar.create Map.empty

  let hostByGroup : DVar<Map<GroupId, Host * Port>> =
    DVar.create Map.empty

  // derived routing tables

  let hostByTopic : DVar<Map<TopicName * Partition, Host * Port>> =
    DVar.combineLatestWith
      (fun topicNodes nodeHosts ->
        topicNodes
        |> Map.toSeq
        |> Seq.choose (fun (tp, n) ->
         match nodeHosts |> Map.tryFind n with
         | Some host -> Some (tp, host)
         | None -> None)
       |> Map.ofSeq)
      nodeByTopic
      hostByNode
    |> DVar.distinct

  let chanByTopic : DVar<Map<(TopicName * Partition), Chan>> =
    (hostByTopic, chanByHost) ||> DVar.combineLatestWith
      (fun topicHosts hostChans ->
        topicHosts
        |> Map.toSeq
        |> Seq.map (fun (t, h) ->
          let chan = Map.find h hostChans in
          t, chan)
        |> Map.ofSeq)

  let chanByGroupId : DVar<Map<GroupId, Chan>> =
    DVar.combineLatestWith
      (fun groupHosts hostChans ->
        groupHosts
        |> Map.toSeq
        |> Seq.map (fun (g, h) ->
          let chan = Map.find h hostChans in
          g, chan)
        |> Map.ofSeq)
      hostByGroup
      chanByHost

  member __.AddChanByHostPort (host:Host, port:Port, ch:Chan) =
    chanByHost |> DVar.update (Map.add (host, port) ch)

  member __.AddHostPortByNodeId (nodeId:NodeId, host:Host, port:Port) =
    hostByNode |> DVar.update (Map.add nodeId (host, port))

  member __.AddGroupCoordinatorHostByGroupId (coordinatorHost:Host, coordinatorPort:Port, groupId:GroupId) =
    hostByGroup |> DVar.updateIfDistinct (Map.add groupId (coordinatorHost, coordinatorPort)) |> ignore

  member __.AddLeaderByTopicPartition (leader:Leader, tn:TopicName, p:Partition) = 
    nodeByTopic |> DVar.update (Map.add (tn, p) (leader))

  
    


/// A connection to a Kafka cluster.
/// This is a stateful object which maintains request/reply sessions with brokers.
/// It acts as a context for API operations, providing filtering and fault tolerance.
type KafkaConn internal (cfg:KafkaConnCfg) =

  static let Log = Log.create "KafkaFunc.Conn"

  // note: must call Connect first thing!
  let [<VolatileField>] mutable bootstrapChanField : Chan =
    Unchecked.defaultof<_>

  let bootstrapChan : Chan =
    fun req -> bootstrapChanField req

  // mutable routing tables

  let chanByHost : DVar<Map<Host * Port, Chan>> =
    DVar.create Map.empty

  let hostByNode : DVar<Map<NodeId, Host * Port>> =
    DVar.create Map.empty

  let nodeByTopic : DVar<Map<TopicName * Partition, NodeId>> =
    DVar.create Map.empty

  let hostByGroup : DVar<Map<GroupId, Host * Port>> =
    DVar.create Map.empty

  // derived routing tables

  let hostByTopic : DVar<Map<TopicName * Partition, Host * Port>> =
    DVar.combineLatestWith
      (fun topicNodes nodeHosts ->
        topicNodes
        |> Map.toSeq
        |> Seq.choose (fun (tp, n) ->
         match nodeHosts |> Map.tryFind n with
         | Some host -> Some (tp, host)
         | None -> None)
       |> Map.ofSeq)
      nodeByTopic
      hostByNode
    |> DVar.distinct

  let chanByTopic : DVar<Map<(TopicName * Partition), Chan>> =
    (hostByTopic, chanByHost) ||> DVar.combineLatestWith
      (fun topicHosts hostChans ->
        topicHosts
        |> Map.toSeq
        |> Seq.map (fun (t, h) ->
          let chan = Map.find h hostChans in
          t, chan)
        |> Map.ofSeq)

  let chanByGroupId : DVar<Map<GroupId, Chan>> =
    DVar.combineLatestWith
      (fun groupHosts hostChans ->
        groupHosts
        |> Map.toSeq
        |> Seq.map (fun (g, h) ->
          let chan = Map.find h hostChans in
          g, chan)
        |> Map.ofSeq)
      hostByGroup
      chanByHost

  let routedChan : Chan =
    DVar.combineLatestWith
      (fun chanByTopic chanByGroup -> Conn.route bootstrapChan chanByTopic chanByGroup)
      chanByTopic
      chanByGroupId
    |> DVar.toFun
    //|> AsyncFunc.catch // TODO: catch recoverable exceptions and initiate recovery
    |> AsyncFunc.mapOutWithInAsync (fun (_,res) -> async {
      // action = RetryAfterMetadataRefresh | RetryAfterSleep | Escalate
      let action = 0
      match res with
      | ResponseMessage.FetchResponse r ->
        for (tn,pmd) in r.topics do          
          for (_,ec,_,_,_) in pmd do
            match ec with
            | ErrorCode.NoError -> ()
            | ErrorCode.NotLeaderForPartition ->
              // refresh metadata, retry
              ()
            | _ ->
              // escalate
              ()
      | ResponseMessage.ProduceResponse r ->
        for (tn,ps) in r.topics do
          for (p,ec,os) in ps do
            match ec with
            | ErrorCode.NoError -> ()
            | ErrorCode.LeaderNotAvailable | ErrorCode.RequestTimedOut -> 
              ()
              
            | ErrorCode.NotLeaderForPartition ->
              // refresh metadata

              ()
            | _ -> ()
        ()
      | _ -> 
        ()

            

      return res })

  /// Connects to the specified host and adds to routing table.
  let connHost (host:Host, port:Port, nodeId:NodeId option) = async {
    let! eps = Dns.IPv4.getEndpointsAsync (host, port)
    let ep = eps.[0] // TODO: handle
    let! ch = Conn.connect (ep, cfg.clientId)
    chanByHost |> DVar.update (Map.add (host, port) ch)
    // TODO: Reload topics DVar here! This hack is for testing.
    // This really need to be done by the cluster not here.
    // Let's establish that the lower level API should always
    // assume the specific connection has been selected. The
    // routing should be moved to the cluster API.
    Log.info "connected to host=%s port=%i node_id=%A" host port nodeId
    //if nodeId.IsSome then
    //    hostByNode |> DVar.update (Map.add nodeId.Value (host, port))
    // Perhaps we can get the metadata here. In a more complete design, we'd
    // want a metadata manager handling these sorts of things. The state of
    // fetching metadata matters and failure to get metadata updates needs to
    // be visible. We'll wrap these into mailbox processors. To this extent
    // we'll have a reliable way to detect errors in a consistent way rather
    // than have too many places in our code to pick them up. DVars should be
    // used to deal with shared state (ETS-like configuration caches). The
    // library for these caches should effectively look like function calls
    // or at least simple values. DVars and MailboxProcessors should not
    // become API.
    // Let's start a new project in this solution which does only the
    // publishing side with the basic cluster processor idea (instead of
    // building on top of the current project, which is overcomplicated).
    //hostByTopic |> DVar.update (Map.add ("test1", 0) (host, port))
    nodeId |> Option.iter (fun nodeId -> hostByNode |> DVar.update (Map.add nodeId (host, port)))
    return ch }

  /// Connects to the specified host unless already connected.
  let connHostNew (host:Host, port:Port, nodeId:NodeId option) = async {    
    match chanByHost |> DVar.get |> Map.tryFind (host, port) with
    | Some ch ->      
      nodeId |> Option.iter (fun nodeId -> hostByNode |> DVar.update (Map.add nodeId (host, port)))
      return ch
    | None -> return! connHost (host, port, nodeId) }

  /// Connects to the first broker in the bootstrap list.
  let connectBootstrap () = async {
    Log.info "discovering bootstrap brokers...|client_id=%s" cfg.clientId
    let! bootstrapChan =
      cfg.bootstrapServers
      |> AsyncSeq.ofSeq
      |> AsyncSeq.tryPickAsync (fun uri -> async {
        //Log.info "connecting....|client_id=%s host=%s port=%i" cfg.clientId uri.Host uri.Port
        try
          let! ch = connHost (uri.Host, uri.Port, None)
          return Some ch
        with ex ->
          Log.error "error connecting to bootstrap host=%s port=%i error=%O" uri.Host uri.Port ex
          return None })
    match bootstrapChan with
    | Some bootstrapChan ->
      return bootstrapChan
    | None ->
      return failwith "unable to connect to bootstrap brokers" }

  /// Connects to the coordinator broker for the specified group and adds to routing table
  let connectGroupCoordinator (groupId:GroupId) = async {
    let! res = Api.groupCoordinator bootstrapChan (GroupCoordinatorRequest(groupId))
    let! ch = connHostNew (res.coordinatorHost, res.coordinatorPort, Some res.coordinatorId)
    hostByGroup |> DVar.updateIfDistinct (Map.add groupId (res.coordinatorHost, res.coordinatorPort)) |> ignore
    return ch }


  /// Gets the channel.
  member internal __.Chan : Chan =
    if isNull routedChan then
      invalidOp "The connection has not been established!"
    routedChan

  /// Connects to a broker from the bootstrap list.
  member internal __.Connect () = async {
    let! ch = connectBootstrap ()
    bootstrapChanField <- ch }

  /// Gets metadata from the bootstrap channel and updates internal routing tables.
  member private __.ApplyMetadata (metadata:MetadataResponse) = async {
    Log.info "applying cluster metadata for topics=%s" (String.concat ", " (metadata.topicMetadata |> Seq.map (fun m -> m.topicName)))
    let hostByNode' =
      metadata.brokers
      |> Seq.map (fun b -> b.nodeId, (b.host, b.port))
      |> Map.ofSeq
    for tmd in metadata.topicMetadata do
      for pmd in tmd.partitionMetadata do
        let (host,port) = hostByNode' |> Map.find pmd.leader // TODO: handle error, but shouldn't happen
        let! _ = connHostNew (host, port, Some pmd.leader)
        nodeByTopic |> DVar.update (Map.add (tmd.topicName, pmd.partitionId) (pmd.leader)) }

  /// Gets metadata from the bootstrap channel and updates internal routing tables.
  member internal this.GetMetadata (topics:TopicName[]) = async {
    Log.info "getting cluster metadata for topics=%s" (String.concat ", " topics)
    let! metadata = Api.metadata bootstrapChan (Metadata.Request(topics))
    do! this.ApplyMetadata metadata
    return metadata }

  /// Gets the group coordinator for the specified group, connects to it, and updates internal routing table.
  member internal __.ConnectGroupCoordinator (groupId:GroupId) =
    connectGroupCoordinator groupId

  interface IDisposable with
    member __.Dispose () = ()

/// Kafka API.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Kafka =

  let [<Literal>] DefaultPort = 9092

  let connAsync (cfg:KafkaConnCfg) = async {
    let conn = new KafkaConn(cfg)
    do! conn.Connect ()
    return conn }

  let conn cfg =
    connAsync cfg |> Async.RunSynchronously

  let connHostAsync (host:string) =
    let ub = UriBuilder("kafka", host, DefaultPort)
    let cfg = KafkaConnCfg.ofBootstrapServers [ub.Uri]
    connAsync cfg

  let connHost host =
    connHostAsync host |> Async.RunSynchronously

  let connHostAndPort host port =
    let ub = UriBuilder("kafka", host, port)
    let cfg = KafkaConnCfg.ofBootstrapServers [ub.Uri]
    let conn = new KafkaConn(cfg)
    conn.Connect() |> Async.RunSynchronously
    conn

  let metadata (c:KafkaConn) (req:Metadata.Request) : Async<MetadataResponse> =
    Api.metadata c.Chan req

  let fetch (c:KafkaConn) (req:FetchRequest) : Async<FetchResponse> =
    Api.fetch c.Chan req

  let produce (c:KafkaConn) (req:ProduceRequest) : Async<ProduceResponse> =
    let chan = c.Chan
    Api.produce chan req

  let offset (c:KafkaConn) (req:OffsetRequest) : Async<OffsetResponse> =
    Api.offset c.Chan req

  let groupCoordinator (c:KafkaConn) (req:GroupCoordinatorRequest) : Async<GroupCoordinatorResponse> =
    Api.groupCoordinator c.Chan req

  let offsetCommit (c:KafkaConn) (req:OffsetCommitRequest) : Async<OffsetCommitResponse> =
    Api.offsetCommit c.Chan req

  let offsetFetch (c:KafkaConn) (req:OffsetFetchRequest) : Async<OffsetFetchResponse> =
    Api.offsetFetch c.Chan req

  let joinGroup (c:KafkaConn) (req:JoinGroup.Request) : Async<JoinGroup.Response> =
    Api.joinGroup c.Chan req

  let syncGroup (c:KafkaConn) (req:SyncGroupRequest) : Async<SyncGroupResponse> =
    Api.syncGroup c.Chan req

  let heartbeat (c:KafkaConn) (req:HeartbeatRequest) : Async<HeartbeatResponse> =
    Api.heartbeat c.Chan req

  let leaveGroup (c:KafkaConn) (req:LeaveGroupRequest) : Async<LeaveGroupResponse> =
    Api.leaveGroup c.Chan req

  let listGroups (c:KafkaConn) (req:ListGroupsRequest) : Async<ListGroupsResponse> =
    Api.listGroups c.Chan req

  let describeGroups (c:KafkaConn) (req:DescribeGroupsRequest) : Async<DescribeGroupsResponse> =
    Api.describeGroups c.Chan req