{-# LANGUAGE NoImplicitPrelude #-}
{-# LANGUAGE OverloadedStrings #-}
module DeckBuildUpgradeQuoteSpec (deckBuildUpgradeQuoteSpecs) where
import Prelude
import Control.Monad (unless)
import Data.Aeson
import Data.Aeson.KeyMap qualified as KM
import Data.Map.Strict qualified as Map
import Data.Text (Text)
import Data.Text qualified as T
import Arkham.Native.DeckBuild.Catalog
import Arkham.Native.DeckBuild.Types (DeckInput(..),Issue(..))
import Arkham.Native.DeckBuild.UpgradeQuote

must :: Either Text a -> IO a
must=either (fail . T.unpack) pure
check :: String -> Bool -> IO ()
check label ok=unless ok(fail label)>>putStrLn("PASS "<>label)
field key (Object o)=KM.lookup key o
field _ _=Nothing
deck :: [(Text,Int)] -> Value
deck slots=object ["investigator_code" .= ("02004"::Text),"slots" .= Map.fromList slots]
completeContext :: Value
completeContext=object ["availableXp" .= (20::Int),"xpAdjustment" .= (0::Int),"exileString" .= (""::Text),"ignoredSlots" .= object [],"contextComplete" .= True]
request old new ctx=object ["previousDeck" .= old,"proposedDeck" .= new,"context" .= ctx]
hasIssue code r=any (\(Issue c _)->c==code) (runIssues r)

deckBuildUpgradeQuoteSpecs :: IO ()
deckBuildUpgradeQuoteSpecs = do
  cat<-must catalogResult
  template<-maybe (fail "Missing actual card") pure $ Map.lookup "01033" $ catalogCards cat
  -- Synthetic arithmetic isolators are explicitly not claimed as original playable cards.
  let mk code name level traits extras=template {cardCode=code,cardName=name,cardLevel=Just level,cardLimit=Just 2,cardPermanent=False,cardRequirements=Nothing
        ,cardRaw=object $ ["xp" .= level,"traits" .= (traits::Text),"real_subname" .= (""::Text)]<>extras}
      zero=mk "q-zero" "Zero" 0 "" []
      low=mk "q-low" "Spell upgrade" 1 "Spell." []
      high=mk "q-high" "Spell upgrade" 4 "Spell." []
      art=mk "q-art" "Spell upgrade" 1 "Spell." []
      ex=mk "q-ex" "Exceptional" 2 "" ["exceptional" .= True,"taboo_xp" .= (1::Int)]
      my=mk "q-my" "Myriad" 1 "" ["myriad" .= True]
      owned=(mk "q-owned" "Size increase" 0 "" []) {cardRequirements=Just $ object ["size" .= (2::Int)]}
      capLow=mk "q-cap-low" "Cap" 0 "" []
      capHigh=(mk "q-cap-high" "Cap" 1 "" []) {cardLimit=Just 1}
      perm=high {cardCode="q-perm",cardPermanent=True}
      allCards=[zero,low,high,art,ex,my,owned,capLow,capHigh,perm]
      testCat=cat {catalogCards=foldr (\c->Map.insert(cardCode c)c) (catalogCards cat) allCards}
      version slots extra marks=do
        d<-must $ decodeQ $ deck slots
        pure $ Version d testCat (Map.fromList extra) marks []
      run old new exile ignored delta=calculate True old new (Map.fromList exile) (Map.fromList ignored) delta >>= Right
      empty=Map.empty
  old<-version [("q-low",1)] [] empty
  new<-version [("q-high",1)] [] empty
  upgraded<-must $ run old new [] [] empty
  check "upgrade same-name price difference" $ runCost upgraded==3
  artV<-version [("q-art",1)] [] empty
  swapped<-must $ run old artV [] [] empty
  check "upgrade equal-level no-subtitle swap free" $ runCost swapped==0
  blank<-version [] [] empty
  exV<-version [("q-ex",2),("q-my",3)] [] empty
  priced<-must $ run blank exV [] [] empty
  check "upgrade exceptional then chained and myriad once" $ runCost priced==11
  arOld<-version [("q-low",1),("04109",2),("08059",1)] [] empty
  arNew<-version [("q-high",1),("04109",2),("08059",1)] [] empty
  ar1<-must $ calculate True arOld arNew empty empty empty
  ar2<-must $ calculate False arOld arNew empty empty empty
  check "upgrade AR Rabbit both orders shared pools" $ runCost ar1==0 && runCost ar2==0 && research(runPools ar1)==0 && rabbit(runPools ar1)==1
  let p=Pools 0 2 0 2 2 2
      (z,pz)=applyUpgradeDiscount True True 0 p
  check "upgrade zero-cost still consumes original discount card eligibility" $ z==0 && researchCards pz==0 && rabbitCards pz==1 && research pz==2
  adaptOld<-version [("02110",1)] [] empty
  adaptNew<-version [("02110",1),("q-zero",2)] [] empty
  adapted<-must $ run adaptOld adaptNew [] [] empty
  check "upgrade Adaptable pays two level-zero swaps" $ runCost adapted==0 && adaptable(runPools adapted)==0
  exOld<-version [("q-high",1),("60531",1)] [] empty
  exNew<-version [("q-high",1),("60531",1)] [] empty
  rebuy<-must $ run exOld exNew [("q-high",1)] [] empty
  check "upgrade exile rebuy uses DejaVu per-copy discount" $ runCost rebuy==3 && deja(runPools rebuy)==2
  fillOld<-version [("q-low",1)] [] empty
  fillNew<-version [("q-zero",1)] [] empty
  filled<-must $ run fillOld fillNew [("q-low",1)] [] empty
  check "upgrade exiled slot allows free zero-level replacement" $ runCost filled==0
  ownedNew<-version [("q-owned",1),("q-zero",2)] [] empty
  ownFill<-must $ run blank ownedNew [] [] empty
  check "upgrade owned size freefill follows original including own zero-cost purchase" $ runCost ownFill==1
  capOldV<-version [("q-cap-low",2)] [] empty
  capNewV<-version [("q-cap-high",1),("q-zero",1)] [] empty
  capFill<-must $ run capOldV capNewV [] [] empty
  check "upgrade tightened name cap frees replacement slot" $ runCost capFill==1
  permV<-version [("q-perm",1),("q-zero",1)] [] empty
  permFill<-must $ run old permV [] [] empty
  check "upgrade nonpermanent to permanent frees replacement slot" $ runCost permFill==3
  ignoredOld<-version [("q-low",1),("08059",1)] [] empty
  ignoredNew<-version [("q-high",1),("08059",1)] [] empty
  ignoredRun<-must $ run ignoredOld ignoredNew [] [("q-high",1)] empty
  check "upgrade ignored same-title copies pay full cost and Rabbit penalty" $ runCost ignoredRun==5
  mixedNew<-version [("q-high",2),("08059",1)] [] empty
  mixed<-must $ run ignoredOld mixedNew [] [] empty
  check "upgrade original mixed Rabbit FIXME remains unsupported" $ hasIssue "quoteMixedRabbitPurchase" mixed
  extraNew<-version [] [("q-high",1)] empty
  extraOld<-version [] [("q-low",1)] empty
  extraRun<-must $ run extraOld extraNew [] [] empty
  check "upgrade extra-deck differential uses same arithmetic" $ runCost extraRun==3
  sharedOld<-version [("02110",1)] [] empty
  sharedNew<-version [("02110",1),("q-zero",1)] [("q-zero",2)] empty
  sharedRun<-must $ run sharedOld sharedNew [] [] empty
  check "upgrade main and extra share remaining Adaptable budget" $ runCost sharedRun==1 && adaptable(runPools sharedRun)==0
  let lowCopy=low {cardCode="q-art2"}
      swapDiff=([(art,1),(lowCopy,1)],[(low,1)])
  check "upgrade swaps each inspect original removals independently" $
    all (\(c,_)->maybe False (\(p,_)->canSwap p c) $ findSwap c $ snd swapDiff) (fst swapDiff)
  gifted<-version [("05040",1)] [] empty
  giftRun<-must $ run blank gifted [] [("05040",1)] empty
  check "upgrade Ace of Rods story gift is excluded before pricing" $ runCost giftRun==0
  customOld<-version [("q-low",1),("04109",1)] [] empty
  customNew<-version [("q-low",1),("04109",1)] [] empty
  customRun<-must $ run customOld customNew [] [] $ Map.singleton "q-low" $ Map.singleton 0 3
  check "upgrade existing MAIN customization delta uses prior Spell traits and discounts" $ runCost customRun==2
  actual<-must $ quoteUpgrade cat $ request (deck [("01033",1)]) (deck [("01033",1)]) completeContext
  missing<-must $ quoteUpgrade cat $ request (deck []) (deck [("01033",1)]) (object ["contextComplete" .= False])
  check "upgrade actual API explicit context and missing-context nonapproval" $ field "purchaseCost" actual==Just(Number 0) && field "affordable" missing==Just Null
  let add key value (Object o)=Object $ KM.insert key value o
      add _ _ v=v
      sideDeck=add "sideSlots" (object ["01033" .= (1::Int)]) $ deck []
      annotated=add "meta" (object ["annotation_01033" .= ("inert"::Text)]) sideDeck
  sideQuote<-must $ quoteUpgrade cat $ request sideDeck annotated completeContext
  check "upgrade inert annotation and normal sideboard do not incur XP" $ field "purchaseCost" sideQuote==Just(Number 0)
  check "upgrade unknown cards and mismatched context reject" $
    isLeft(quoteUpgrade cat $ request (deck []) (deck [("missing-quote-card",1)]) completeContext)
    && isLeft(quoteUpgrade cat $ request (deck []) (add "xp" (Number 99) $ deck []) completeContext)
  let jim=object ["investigator_code" .= ("90049"::Text),"slots" .= object ["01033" .= (1::Int)]
        ,"meta" .= object ["extra_deck" .= ("01033"::Text)]]
      exContext=add "exileString" (String "01033") completeContext
  ambiguous<-must $ quoteUpgrade cat $ request jim jim exContext
  check "upgrade same exiled code in main and extra cannot certify budget" $ field "supported" ambiguous==Just(Bool False) && field "affordable" ambiguous==Just Null
  putStrLn "DECKBUILD_UPGRADE_QUOTE_COMPLETED"
 where
  isLeft (Left _)=True
  isLeft _=False
  findSwap _ []=Nothing
  findSwap c (x@(p,_):xs)=if canSwap p c then Just x else findSwap c xs
