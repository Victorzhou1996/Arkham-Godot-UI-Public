module Arkham.Act.Cards.TheScarletKeys.DealingsInTheDark.SearchForTheTalisman (searchForTheTalisman) where

import Arkham.Ability
import Arkham.Act.CardDefs.TheScarletKeys.DealingsInTheDark qualified as Cards
import Arkham.Act.Import.Lifted
import Arkham.Agenda.CardDefs.TheScarletKeys.DealingsInTheDark qualified as Agendas
import Arkham.Campaigns.TheScarletKeys.Concealed.Helpers
import Arkham.Campaigns.TheScarletKeys.Key.Cards qualified as ScarletKeys
import Arkham.Campaigns.TheScarletKeys.Key.Matcher
import Arkham.Campaigns.TheScarletKeys.Key.Types
import Arkham.Card
import Arkham.Direction
import Arkham.Helpers.GameValue (perPlayer)
import Arkham.Investigator.Types (Field (..))
import Arkham.Location.CardDefs.TheScarletKeys.DealingsInTheDark qualified as Locations
import Arkham.Location.Grid
import Arkham.Matcher
import Arkham.Message.Lifted.Choose
import Arkham.Message.Lifted.Move
import Arkham.Modifier
import Arkham.Placement
import Arkham.Projection
import Arkham.Scenarios.TheScarletKeys.DealingsInTheDark.Helpers
import Arkham.Story.CardDefs.TheScarletKeys.DealingsInTheDark qualified as Stories

newtype SearchForTheTalisman = SearchForTheTalisman ActAttrs
  deriving anyclass (IsAct, HasModifiersFor)
  deriving newtype (Show, Eq, ToJSON, FromJSON, Entity)

searchForTheTalisman :: ActCard SearchForTheTalisman
searchForTheTalisman = act (2, A) SearchForTheTalisman Cards.searchForTheTalisman Nothing

instance HasAbilities SearchForTheTalisman where
  getAbilities = actAbilities \a ->
    [ restricted
        a
        1
        ( oneOf
            [ InvestigatorsAtHaveClues
                (LocationWithModifier $ ScenarioModifier "eastGate")
                (AtLeast $ PerPlayer 8)
            , exists (StoryWithModifier $ ScenarioModifier "cultHasEnoughClues")
            ]
        )
        $ Objective
        $ forced AnyWindow
    ]

instance RunMessage SearchForTheTalisman where
  runMessage msg a@(SearchForTheTalisman attrs) = runQueueT $ case msg of
    UseThisAbility _iid (isSource attrs -> True) 1 -> do
      advanceVia #other attrs (attrs.ability 1)
      pure a
    AdvanceAct (isSide B attrs -> True) _ _ -> do
      cultReachedTheTalisman <- selectAny $ StoryWithModifier $ ScenarioModifier "cultHasEnoughClues"
      if cultReachedTheTalisman
        then do
          cultists <- select $ EnemyWithMostClues #cultist
          if null cultists
            then do
              -- Official October 2023 ruling: with no Cultist in play or in
              -- the shadows, find one in the encounter deck/discard and spawn
              -- it at the east gate before attaching the key and continuing.
              lead <- getLead
              findEncounterCard lead attrs $ card_ $ #enemy <> #cultist
            else leadChooseOneM do
              targets cultists $ createScarletKeyAt_ ScarletKeys.theTwistedAntiprism . AttachedToEnemy
        else do
          investigators <- select $ InvestigatorAt $ LocationWithModifier $ ScenarioModifier "eastGate"
          leadChooseOneM do
            targets investigators $ createScarletKeyAt_ ScarletKeys.theTwistedAntiprism . AttachedToInvestigator

      doStep (-2) msg
      pure a
    FoundEncounterCard _ (isTarget attrs -> True) card -> do
      eastGate <- selectJust $ LocationWithModifier $ ScenarioModifier "eastGate"
      -- An explicit location spawn is not an encounter draw: it must not
      -- resolve concealed and return this replacement bearer to the shadows.
      cultist <- createEnemyAt card eastGate
      createScarletKeyAt_ ScarletKeys.theTwistedAntiprism $ AttachedToEnemy cultist
      pure a
    DoStep (-2) msg'@(AdvanceAct (isSide B attrs -> True) _ _) -> do
      cultReachedTheTalisman <- selectAny $ StoryWithModifier $ ScenarioModifier "cultHasEnoughClues"

      do_ msg'
      selectEach OutOfGameLocation (push . ReturnLocationToGame)

      fetchCard Locations.galataDocks >>= placeLocationInGrid_ (Pos (-2) 1)
      doStep (-1) msg'

      when cultReachedTheTalisman do
        cultistClues <- getCluesPossesedByTheCult
        playerClues <- selectSum InvestigatorClues Anyone
        n <- perPlayer 1
        let x = max 0 (cultistClues - playerClues) `div` n
        when (x > 0) $ doStep x msg'

      lead <- getLead
      eachInvestigator (`loseAllClues` attrs)
      -- delayed so that all enemies are in play
      selectJust (storyIs Stories.theUnveiling) >>= flipOverBy lead attrs
      agenda <- getSetAsideCard Agendas.theChase
      push $ SetCurrentAgendaDeck 1 [agenda]
      toDiscard GameSource attrs
      pure a
    Do msg'@(AdvanceAct (isSide B attrs -> True) _ _) -> do
      concealed <- selectMap (.id) ConcealedCardAny
      lead <- getLead
      leadChooseOneM $ targets concealed \card -> do
        -- Act 2b reveals the mini-cards; it does not expose them. Move the
        -- matching enemy out of the shadows without opening expose reactions.
        moveFromShadows lead attrs card
        do_ msg'
      pure a
    DoStep x msg'@(AdvanceAct (isSide B attrs -> True) _ _) | x > 0 -> do
      skey <- selectJust $ scarletKeyIs ScarletKeys.theTwistedAntiprism
      placement <- field ScarletKeyPlacement skey
      docks <- selectJust $ locationIs Locations.galataDocks
      case placement of
        AttachedToEnemy eid -> do
          moveTowards attrs eid docks
          doStep (x - 1) msg'
        _ -> pure ()
      pure a
    DoStep (-1) (AdvanceAct (isSide B attrs -> True) _ _) -> do
      selectEach (enemy_ #cultist) (removeAllClues attrs)
      hagia <- selectJust $ locationIs Locations.hagiaSophia
      entrance <- selectJust $ LocationInPosition (Pos 1 0)
      push $ PlacedLocationDirection hagia LeftOf entrance
      pure a
    _ -> SearchForTheTalisman <$> liftRunMessage msg attrs
