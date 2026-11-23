module NativeAuthorSpec (nativeAuthorSpecs) where

import Arkham.Prelude hiding (assert)
import Arkham.Card.CustomCard
import Arkham.Card.CardType
import Arkham.Decklist.Type (ArkhamDBDecklist (..))
import Arkham.Difficulty
import Arkham.Game (Game (..))
import Arkham.Id
import Arkham.Question (Question (ChooseDeck))
import Data.Aeson (Result (..))
import Arkham.Native.Author qualified as Author
import Arkham.Native.Answer
import Arkham.Native.Session
import Data.Map.Strict qualified as Map
import Data.Aeson.KeyMap qualified as KM
import Data.Text qualified as T
import Data.ByteString.Lazy.Char8 qualified as BL
import Control.Exception qualified as E
import System.Exit (exitFailure)

check :: Text -> Bool -> IO ()
check label ok=if ok then putStrLn $ "PASS "<>label else putStrLn ("FAIL "<>label)>>exitFailure
right :: Either Text a -> IO a
right=either (\e->putStrLn e>>exitFailure) pure

-- Match the real FFI export boundary: PlayerCard's original JSON encoder reads
-- the custom registry for errata. Force every snapshot under its OWN registry.
safeExport :: Snapshot -> IO BL.ByteString
safeExport snapshot=boundary (gameCustomCards $ snapshotGame snapshot) (do
  let bytes=exportSnapshot snapshot
  _ <- E.evaluate $ BL.length bytes
  pure bytes) >>= right

safeValue :: Snapshot -> IO Value
safeValue snapshot=safeExport snapshot >>= right . first T.pack . eitherDecode

nativeAuthorSpecs :: PlayerId -> (PlayerId -> ArkhamDBDecklist -> Snapshot -> IO Answer) -> IO ()
nativeAuthorSpecs player choose=do
  decks<-eitherDecodeFileStrict @[ArkhamDBDecklist] "arkham-core/data/community-starter-decks.json">>=either (\e->putStrLn (T.pack e)>>exitFailure) pure
  let deck=headEx decks
      cardValue=object ["def" .= object ["cardCode" .= ("c*native-author0"::Text),"name" .= object ["title" .= ("Native author"::Text),"subtitle" .= Null],"cardType" .= AssetType,"art" .= ("*native-author0"::Text),"meta" .= object ["futureAdvanced" .= object ["int64" .= (9223372036854775806::Int64),"template" .= ("$source"::Text)]]],"art" .= Null]
      card :: CustomCard
      card=case fromJSON cardValue of Success result->result;Error reason->error reason
      raw=decodeUtf8 $ BL.toStrict $ encode $ object ["format" .= ("arkham-author-package"::Text),"version" .= (1::Int),"cards" .= [card]]
      digest=Author.rawDigest raw
      rejected=either (const True) (const False)
  package<-right $ Author.validatePackage raw digest
  check "author exact UTF8 digest and advanced Int64 metadata retained" $ Author.authorRaw package==raw&&Map.size (Author.authorCards package)==1
  check "author wrong content digest rejected" $ rejected $ Author.validatePackage raw (T.replicate 64 "0")
  let duplicate="{\"format\":\"arkham-author-package\",\"version\":1,\"cards\":[],\"cards\":[]}"
  check "author duplicate JSON rejected before Aeson object collapse" $ rejected $ Author.validatePackage duplicate (Author.rawDigest duplicate)
  let conflict=card{customCardArt=Just $ "sha256:"<>T.replicate 64 "1"}
  check "author same code different art conflicts" $ rejected $ Author.mergePackage (Map.singleton "*native-author0" conflict) package
  let aliasValue=case cardValue of
        Object o | Just (Object def)<-KM.lookup "def" o->Object $ KM.insert "futureRoot" (Bool True) $ KM.insert "def" (Object $ KM.insert "cardCode" (toJSON $ arkhamBuildCustomCardCode "01234567") $ KM.insert "meta" (object ["arkhamBuildId" .= ("01234567"::Text),"futureAdvanced" .= object ["template" .= ("$source"::Text)]]) def) o
        _->error "card object"
      aliasRaw=decodeUtf8 $ BL.toStrict $ encode $ object ["format" .= ("arkham-author-package"::Text),"version" .= (1::Int),"cards" .= [aliasValue]]
  aliasPackage<-right $ Author.validatePackage aliasRaw (Author.rawDigest aliasRaw)
  check "same card primary equal to ArkhamBuild alias is deduplicated and ignored field reported" $ length (Author.packageCards aliasPackage)==1&&Author.authorIgnoredFields aliasPackage==["$.cards[0].futureRoot"]
  check "no nonexistent original custom Act or Agenda builder advertised" $ ActType `notElem` Author.supportedTypes&&AgendaType `notElem` Author.supportedTypes
  let install s=fst <$> (applyAuthorInstall s player (snapshotRevision s) True raw digest>>=right)
      step s=choose player deck s >>= \a -> fst <$> (applyAnswer s player (snapshotRevision s) a>>=right)
      untilBoard n s
        | Object o<-debugView (snapshotGame s) player,Just (Object workbench)<-KM.lookup "workbench" o,Just (Bool True)<-KM.lookup "available" workbench=pure s
        | n<=0=error "author board not reached"
        | otherwise=step s>>=untilBoard (n-1)
  (created,_)<-createSession (CreateRequest Nothing (Just "01104") 201020 [player] Standard False False)>>=right
  -- The original ChooseDeck remains the sole recipient/permission authority.
  let toDeck n s=case unwrapQuestion <$> Map.lookup player (gameQuestion $ snapshotGame s) of
        Just ChooseDeck->pure s
        _ | n>0->step s>>=toDeck (n-1)
        _->error "author original ChooseDeck not reached"
  deckBasis<-toDeck (30::Int) created
  let withCard=deck{slots=Map.insert "*native-author0" 1 deck.slots}
      answer=DeckListAnswer withCard player
  (chosen,_)<-applyAuthorAnswer deckBasis player (snapshotRevision deckBasis) True raw digest answer>>=right
  importedDeck<-importSnapshot (exportSnapshot deckBasis)>>=right
  (chosenAgain,_)<-applyAuthorAnswer importedDeck player (snapshotRevision importedDeck) True raw digest answer>>=right
  check "author initial deck uses original answer with complete package embedded" $ Map.member "*native-author0" (gameCustomCards $ snapshotGame chosen)
  chosenBytes<-safeExport chosen
  chosenAgainBytes<-safeExport chosenAgain
  check "author deck after export-import exact full Game queue RNG" $ chosenBytes==chosenAgainBytes
  disabled<-applyAuthorAnswer deckBasis player (snapshotRevision deckBasis) False raw digest answer
  check "author execution requires explicit trust confirmation" $ rejected disabled
  basis<-untilBoard (120::Int) created
  installed<-install basis
  imported<-importSnapshot (exportSnapshot basis)>>=right
  replayed<-install imported
  installedBytes<-safeExport installed
  replayedBytes<-safeExport replayed
  check "author install direct and imported basis produce identical whole snapshot" $ installedBytes==replayedBytes
  repeated<-install installed
  check "author repeated install does not duplicate definitions" $ gameCustomCards (snapshotGame installed)==gameCustomCards (snapshotGame repeated)
  let iid=fst $ headEx $ Map.toList (gameEntities $ snapshotGame installed).investigators
      create=object ["operation" .= ("createCard"::Text),"cardCode" .= ("c*native-author0"::Text),"placement" .= ("hand"::Text),"investigatorId" .= iid,"deck" .= Null]
  (handed,_)<-applyDebug installed player (snapshotRevision installed) True create>>=right
  check "installed custom definition resolves through original CardGen" $ Map.size (gameCards $ snapshotGame handed)==Map.size (gameCards $ snapshotGame installed)+1
  check "author candidate leaves old snapshot definitions unchanged" $ Map.null $ gameCustomCards $ snapshotGame basis
  beforeValue<-safeValue basis
  installedValue<-safeValue installed
  handedValue<-safeValue handed
  deckBeforeValue<-safeValue deckBasis
  deckAfterValue<-safeValue chosen
  BL.putStrLn $ "AUTHOR_FIXTURE "<>encode (object ["before" .= beforeValue,"packageText" .= raw,"packageSha256" .= digest,"installed" .= installedValue,"handed" .= handedValue,"deckBefore" .= deckBeforeValue,"deck" .= withCard,"deckAfter" .= deckAfterValue])
  putStrLn "AUTHOR_INTEGRATION_COMPLETED"
