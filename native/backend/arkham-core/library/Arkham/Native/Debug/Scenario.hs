-- Finite adapters for existing original scenario debug consumers. A client
-- never supplies ScenarioSpecific keys, Messages, Sources or arbitrary meta.
module Arkham.Native.Debug.Scenario (operationNames,prepare,view) where

import Arkham.Prelude hiding (view)
import Arkham.Game (Game (..))
import Arkham.Game.Utils (modeScenario)
import Arkham.Entities (Entities (..))
import Arkham.Classes.Entity (toAttrs)
import Arkham.Agenda.Types (AgendaAttrs (..))
import Arkham.Story.Types (StoryAttrs (..))
import Arkham.Scenario.Types (ScenarioAttrs (..))
import Arkham.ScenarioLogKey (ScenarioCountKey (..))
import Arkham.SortedPair
import Arkham.Id
import Arkham.Message
import Arkham.Source
import Arkham.Target
import Arkham.Token qualified as Token
import Arkham.TokenBag
import Arkham.ChaosToken.Types
import Arkham.Native.Debug.Actions qualified as Actions
import Arkham.Native.Debug.Workbench qualified as Workbench
import Data.Aeson.Types (Parser,parseEither,parseMaybe)
import Data.Aeson.Key (Key)
import Data.Aeson.KeyMap qualified as KM
import Data.Map.Strict qualified as Map

data BagOwner = NamedBag Text | StoryBag StoryId

operationNames :: [Text]
operationNames=["setScenarioCount","scenarioDebugAction","editAuxiliaryBag","removeScenarioBarrier","agendaDoom"]

scenarioAttrs :: Game -> Maybe ScenarioAttrs
scenarioAttrs game=toAttrs <$> modeScenario (gameMode game)

countChoices :: Game -> [(Text,Text,ScenarioCountKey)]
countChoices game=case fmap scenarioId $ scenarioAttrs game of
  Just "83001"->abyss
  Just "83016"->abyss
  Just "04277"->depth
  Just "53059"->depth
  Just "11688a"->[("CthulhuRage","克苏鲁之怒",CthulhuRage)]
  _->[]
 where
  abyss=[("StrengthOfTheAbyss","深渊之力",StrengthOfTheAbyss)]
  depth=[("CurrentDepth","当前深度",CurrentDepth)]

specialChoices :: Game -> [Text]
specialChoices game=case fmap scenarioId $ scenarioAttrs game of
  Just "85001"->["blobRealityAcid","clearRealityAcid"]
  Just "11688a"->["drawCthulhu"]
  _->[]

auxiliaryBags :: Game -> Map Text (BagOwner,CustomChaosBag)
auxiliaryBags game=Map.fromList $
  [("scenario:"<>key,(NamedBag key,bag))|attrs<-maybeToList $ scenarioAttrs game,(key,bag)<-Map.toAscList $ scenarioCustomChaosBags attrs]
  <>[("story:"<>tshow sid,(StoryBag sid,bag))|(sid,story)<-Map.toAscList $ entitiesStories $ gameEntities game
    ,Just bag<-[parseMaybe parseJSON $ storyMeta $ toAttrs story]]

barrierChoices :: Game -> [(Text,LocationId,LocationId,Int)]
barrierChoices game=case scenarioAttrs game of
  Just attrs | scenarioId attrs `elem` ["07123","10626"]
    ,Object meta<-scenarioMeta attrs,Just raw<-KM.lookup "barriers" meta
    ,Just barriers<-(parseMaybe parseJSON raw :: Maybe (Map (SortedPair LocationId) Int))->
      [(tshow a<>"--"<>tshow b,a,b,n)|(pair,n)<-Map.toAscList barriers,let (a,b)=unSortedPair pair
        ,n>0,a/=b,all (`Map.member` entitiesLocations (gameEntities game)) [a,b]]
  _->[]

facesFor :: CustomChaosBag -> [ChaosTokenFace]
facesFor bag=nub $ Workbench.ordinaryFaces<>map bagTokenFace (allBagTokens bag)

prepare :: Game -> PlayerId -> Value -> Either Text Message
prepare game player raw=do
  Actions.stablePlayerWindow game player
  objectValue<-case raw of Object value->Right value;_->Left "Expected finite scenario command"
  operation<-first fromString $ parseEither ((.: "operation") :: Object -> Parser Text) objectValue
  let parse :: [Key] -> (Object -> Parser a) -> Either Text a
      parse fields parser=first fromString $ parseEither (\o->Actions.exact o fields>>parser o) objectValue
  case operation of
    "setScenarioCount"->do
      (ident,amount)<-parse ["operation","key","amount"] \o->(,) <$> o .: "key" <*> o .: "amount"
      (_,_,key)<-maybe (Left "Count is not offered by this actual scenario") Right $ find (\(name,_,_)->name==ident) $ countChoices game
      unless (amount>=0 && amount<=999999) $ Left "Scenario count must be a bounded nonnegative integer"
      pure $ ScenarioCountSet key amount
    "scenarioDebugAction"->do
      (action,faces)<-parse ["operation","action","faces"] \o->(,) <$> (o .: "action" :: Parser Text) <*> (o .: "faces" :: Parser [ChaosTokenFace])
      unless (action `elem` specialChoices game) $ Left "Special debug action is not offered by this scenario"
      case action of
        "blobRealityAcid"->do
          unless (length faces==2 && all (`elem` Workbench.ordinaryFaces) faces) $ Left "Reality Acid requires exactly two ordinary token faces"
          pure $ ScenarioSpecific "blobSetDebugRealityAcidTokens" $ toJSON faces
        "clearRealityAcid"->unless (null faces) (Left "Clear accepts no token payload") >> pure (ScenarioSpecific "blobClearDebugRealityAcidTokens" Null)
        "drawCthulhu"->unless (null faces) (Left "Draw accepts no token payload") >> pure (ScenarioSpecific "debugDrawCthulhuCard" Null)
        _->Left "Unknown finite special action"
    "editAuxiliaryBag"->do
      (ident,action,face,tokenId)<-parse ["operation","bag","action","face","tokenId"] \o->
        (,,,) <$> o .: "bag" <*> (o .: "action" :: Parser Text) <*> o .: "face" <*> o .: "tokenId"
      (owner,bag)<-maybe (Left "Unknown current auxiliary bag") Right $ Map.lookup ident $ auxiliaryBags game
      let noId=unless (isNothing tokenId) $ Left "This bag action accepts no token identity"
          noFace=unless (isNothing face) $ Left "This bag action accepts no token face"
          memberToken pile=unless (maybe False (\tid->any ((==tid) . bagTokenId) pile) tokenId) $ Left "Token is not in the offered bag compartment"
      choice<-case action of
        "next"->do
          noId
          unless (maybe False (\f->any ((==f) . bagTokenFace) $ bagTokens bag) face) $ Left "Next token must be in the available pile"
          pure $ toJSON face
        "clear"->noId>>noFace>>pure Null
        "add"->do
          noId
          unless (maybe False (`elem` facesFor bag) face && length (allBagTokens bag)<500) $ Left "Unsupported face or full auxiliary bag"
          pure $ object ["action" .= ("add"::Text),"face" .= face]
        "remove"->noFace>>memberToken (bagTokens bag)>>pure (object ["action" .= ("remove"::Text),"id" .= tokenId])
        "setAside"->noFace>>memberToken (bagTokens bag)>>pure (object ["action" .= ("setAside"::Text),"id" .= tokenId])
        "return"->noFace>>memberToken (bagSetAside bag)>>pure (object ["action" .= ("return"::Text),"id" .= tokenId])
        "returnSetAside"->noId>>noFace>>pure (object ["action" .= ("returnSetAside"::Text)])
        _->Left "Unknown finite auxiliary bag action"
      pure $ case owner of
        NamedBag key->ScenarioSpecific "debugTokenBag" $ object ["key" .= key,"next" .= choice]
        StoryBag sid->SendMessage (StoryTarget sid) $ ScenarioSpecific "debugTokenBag" choice
    "removeScenarioBarrier"->do
      ident<-parse ["operation","barrier"] (.: "barrier")
      (_,a,b,_)<-maybe (Left "Barrier is no longer offered") Right $ find (\(key,_,_,_)->key==ident) $ barrierChoices game
      pure $ ScenarioCountDecrementBy (Barriers a b) 1
    "agendaDoom"->do
      (aid,amount)<-parse ["operation","agendaId","amount"] \o->(,) <$> (Actions.canonical=<<o .: "agendaId") <*> o .: "amount"
      agenda<-maybe (Left "Unknown current agenda") Right $ Map.lookup aid $ entitiesAgendas $ gameEntities game
      unless (amount `elem` [-1,1,5] && (amount>=0 || agendaDoom (toAttrs agenda)>0)) $ Left "Agenda doom action is unavailable"
      pure $ if amount>0 then PlaceTokens GameSource (AgendaTarget aid) Token.Doom amount else RemoveTokens GameSource (AgendaTarget aid) Token.Doom 1
    _->Left "Unknown finite scenario operation"

view :: Game -> PlayerId -> Value
view game player=object
  ["schemaVersion" .= (1::Int),"available" .= (case Actions.stablePlayerWindow game player of Right ()->True;_->False)
  ,"operations" .= operationNames
  ,"scenario" .= fmap scenarioId attrs
  ,"counts" .= [object ["id" .= ident,"label" .= label,"value" .= maybe 0 (Map.findWithDefault 0 key . scenarioCounts) attrs
      ,"minimum" .= (0::Int),"maximum" .= (999999::Int)]|(ident,label,key)<-countChoices game]
  ,"specialActions" .= specialChoices game
  ,"faces" .= Workbench.ordinaryFaces
  ,"realityAcidTokens" .= (case attrs of Just a|Object meta<-scenarioMeta a->fromMaybe Null $ KM.lookup "debugRealityAcidTokens" meta;_->Null)
  ,"bags" .= [object ["id" .= ident,"owner" .= (case owner of NamedBag key->"场景 · "<>key;StoryBag sid->"剧情 · "<>tshow sid)
      ,"state" .= bag,"faces" .= facesFor bag]|(ident,(owner,bag))<-Map.toAscList $ auxiliaryBags game]
  ,"barriers" .= [object ["id" .= ident,"from" .= a,"to" .= b,"value" .= n]|(ident,a,b,n)<-barrierChoices game]
  ,"agendas" .= [object ["id" .= aid,"doom" .= agendaDoom (toAttrs agenda)]|(aid,agenda)<-Map.toAscList $ entitiesAgendas $ gameEntities game]
  ,"agendaPolicy" .= ("original-place-remove-token-effects-not-direct-state"::Text)]
 where attrs=scenarioAttrs game
